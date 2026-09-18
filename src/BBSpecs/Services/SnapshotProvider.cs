using BBSpecs.Models;
using DriveInfo = BBSpecs.Models.DriveInfo;

namespace BBSpecs.Services;

/// <summary>
/// Orchestrates a snapshot. Everything platform-independent lives here (the
/// static/live caching split, the network layer and the overall verdict), while
/// each operating system supplies the actual readings through the abstract
/// members below.
/// </summary>
public abstract class SnapshotProvider : IDisposable
{
    /// <summary>
    /// Model names, slot layout and BIOS versions don't change while the app is
    /// open, but drives get plugged in and monitors get swapped, so the "static"
    /// half is re-read on this interval rather than exactly once.
    /// </summary>
    private static readonly TimeSpan StaticLifetime = TimeSpan.FromSeconds(30);

    private DateTime _staticStamp = DateTime.MinValue;
    private SystemInfo _system = new();
    private CpuInfo _cpu = new();
    private List<GpuInfo> _gpus = [];
    private MemoryInfo _memory = new();
    private List<DriveInfo> _drives = [];
    private BoardInfo _board = new();

    public NetworkService Network { get; }

    protected SnapshotProvider(NetworkService network) => Network = network;

    public abstract bool SensorsReady { get; }
    public abstract string? SensorNote { get; }

    /// <summary>Where to go to fix <see cref="SensorNote"/>, if anywhere.</summary>
    public virtual string? SensorHelpUrl => null;

    /// <summary>Opens sensor handles. Called once, on the collector thread.</summary>
    public virtual void Start() { }

    /// <summary>
    /// Asks the collector to reopen the sensor layer on its next pass. Used after
    /// installing the driver, so temperatures appear without a restart. It must
    /// happen on the collector thread, which is why this only sets a flag.
    /// </summary>
    public void RequestSensorReload() => _reloadWanted = true;

    private volatile bool _reloadWanted;

    public Snapshot Build(string version)
    {
        if (_reloadWanted)
        {
            _reloadWanted = false;
            Try(ReloadSensors);
            // Model names don't change, but a newly readable sensor might, so
            // let the static half refresh too.
            _staticStamp = DateTime.MinValue;
        }

        RefreshSensors();

        if (DateTime.UtcNow - _staticStamp > StaticLifetime)
        {
            // A failure here keeps the previous values rather than blanking the UI.
            _system = Safe(ReadSystem, _system);
            _cpu = Safe(ReadCpu, _cpu);
            _gpus = Safe(ReadGpus, _gpus);
            _memory = Safe(ReadMemory, _memory);
            _drives = Safe(ReadDrives, _drives);
            _board = Safe(ReadBoard, _board);
            _staticStamp = DateTime.UtcNow;
        }

        var snapshot = new Snapshot
        {
            Version = version,
            IsAdmin = Elevation.IsElevated,
            SensorsReady = SensorsReady,
            SensorNote = SensorNote,
            SensorHelpUrl = SensorHelpUrl,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            System = _system,
            Cpu = _cpu,
            Gpus = _gpus,
            Memory = _memory,
            Drives = _drives,
            Board = _board,
            Network = Safe(Network.Collect, new NetworkInfo()),
            Fans = Safe(ReadFans, []),
        };

        Try(() => ApplyCpuLive(snapshot.Cpu));
        Try(() => ApplyGpuLive(snapshot.Gpus));
        Try(() => ApplyMemoryLive(snapshot.Memory));
        Try(() => ApplyDriveLive(snapshot.Drives));
        Try(() => ApplyBoardLive(snapshot.Board));

        snapshot.System.UptimeHours = ReadUptimeHours();
        ApplyOverallVerdict(snapshot);

        // Both of these read the finished snapshot, so they run last.
        snapshot.Upgrades = Safe(() => Upgrades.For(snapshot), []);
        snapshot.SpecsText = Safe(() => SpecsText.Build(snapshot), "");

        return snapshot;
    }

    // ---- platform hooks --------------------------------------------------------

    protected abstract void RefreshSensors();

    /// <summary>Closes and reopens the sensor layer. Called on the collector thread.</summary>
    protected virtual void ReloadSensors() { }

    protected abstract SystemInfo ReadSystem();
    protected abstract CpuInfo ReadCpu();
    protected abstract List<GpuInfo> ReadGpus();
    protected abstract MemoryInfo ReadMemory();
    protected abstract List<DriveInfo> ReadDrives();
    protected abstract BoardInfo ReadBoard();
    protected abstract List<FanInfo> ReadFans();

    protected abstract void ApplyCpuLive(CpuInfo cpu);
    protected abstract void ApplyGpuLive(List<GpuInfo> gpus);
    protected abstract void ApplyMemoryLive(MemoryInfo memory);
    protected abstract void ApplyDriveLive(List<DriveInfo> drives);
    protected abstract void ApplyBoardLive(BoardInfo board);

    /// <summary>Hours since boot. Windows can use the tick count; Linux reads /proc.</summary>
    protected virtual double ReadUptimeHours() => Environment.TickCount64 / 3_600_000.0;

    // ---- shared ----------------------------------------------------------------

    private static void ApplyOverallVerdict(Snapshot s)
    {
        GpuInfo? gpu = s.Gpus.FirstOrDefault();
        DriveInfo? systemDrive = s.Drives.FirstOrDefault(d => d.IsSystemDrive) ?? s.Drives.FirstOrDefault();

        (Verdict overall, int score, List<string> highlights, List<string> watchouts) =
            Verdicts.RateSystem(
                s.Cpu.Verdict,
                gpu?.Verdict ?? Verdict.Unknown(),
                s.Memory.Verdict,
                systemDrive?.Verdict ?? Verdict.Unknown(),
                s.Cpu.ShortName,
                gpu?.Name ?? "Unknown",
                s.Memory.TotalGb,
                systemDrive?.Kind ?? "Unknown");

        s.System.Overall = overall;
        s.System.Score = score;
        s.System.Highlights = highlights;
        s.System.Watchouts = watchouts;
    }

    protected static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    protected static void Try(Action action)
    {
        try { action(); }
        catch { /* one unreadable sensor shouldn't cost us the whole snapshot */ }
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
