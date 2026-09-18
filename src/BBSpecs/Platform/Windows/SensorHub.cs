using BBSpecs.Services;
using LibreHardwareMonitor.Hardware;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/>. This is the only place
/// that talks to the sensor library, and every call into it happens on the single
/// collector thread: the underlying library is not thread-safe.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _open;

    /// <summary>
    /// True only when sensors are actually producing numbers.
    ///
    /// Opening the library succeeds even when it can't read anything: the CPU
    /// appears with its full list of sensors and every one of them reads null.
    /// That happens without Administrator, and also when Windows blocks the
    /// kernel driver outright. Reporting "ready" in that state would leave the
    /// user staring at empty dials with no explanation.
    /// </summary>
    public bool Ready { get; private set; }

    public string? Note { get; private set; }

    /// <summary>A page that fixes the problem in <see cref="Note"/>, when one exists.</summary>
    public string? HelpUrl { get; private set; }

    public SensorHub()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,   // we read throughput from .NET instead
            IsControllerEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = true,
        };
    }

    public void Start()
    {
        try
        {
            _computer.Open();
            _open = true;
        }
        catch (Exception ex)
        {
            Ready = false;
            Note = "Sensor access failed: " + ex.Message;
        }
    }

    /// <summary>Refreshes every sensor value. Cheap enough to call once a second.</summary>
    public void Refresh()
    {
        if (!_open) return;

        try
        {
            _computer.Accept(_visitor);
            AssessReadiness();
        }
        catch (Exception ex)
        {
            Ready = false;
            Note = "Sensor refresh failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Decides whether the readings are real, and if not, says why in terms the
    /// user can act on.
    /// </summary>
    private void AssessReadiness()
    {
        IHardware? cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu is null)
        {
            Ready = false;
            Note = "No processor sensors were found on this machine.";
            return;
        }

        // Temperature is the reading that needs the driver. Load comes from
        // Windows itself and keeps working regardless, so it proves nothing.
        bool anyTemperature = AllSensors(cpu)
            .Any(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);

        if (anyTemperature)
        {
            Ready = true;
            Note = null;
            HelpUrl = null;
            return;
        }

        Ready = false;
        HelpUrl = null;

        if (!Elevation.IsElevated)
        {
            Note = NotElevatedNote;
            return;
        }

        // Check this before blaming anything else: without PawnIO the sensors all
        // read null no matter what else is configured, and it looks identical to
        // a permissions or driver-blocking problem.
        if (!PawnIo.IsInstalled)
        {
            Note = "Processor temperature is read through PawnIO, a small free driver that " +
                   "isn't installed on this PC. It's open source, signed by Microsoft, and " +
                   "installs in under a minute: temperatures then appear on their own. " +
                   "Everything else on this page already works without it.";
            HelpUrl = PawnIo.DownloadUrl;
            return;
        }

        Note = ElevatedFailureNote();
    }

    private const string NotElevatedNote =
        "Temperatures need Administrator. Close BBSpecs and start it again with " +
        "\"Run as administrator\".";

    /// <summary>
    /// Running as Administrator and still getting nothing means the low-level
    /// driver didn't load. Rather than assert one cause, name the one we can
    /// actually detect and list the rest in order of likelihood.
    /// </summary>
    private static string ElevatedFailureNote()
    {
        string? rival = ConflictingTool();

        if (rival is not null)
        {
            return $"The sensor driver couldn't start, and {rival} is running. " +
                   "Only one program at a time can hold this kind of hardware access, and " +
                   $"{rival} takes it on startup. Closing it (including from the system tray) " +
                   "and reopening BBSpecs usually fixes this.";
        }

        return "The sensor driver couldn't start. The usual causes are Core Isolation " +
               "(Windows Security, Device security, Core isolation details, Memory integrity), " +
               "Microsoft's Vulnerable Driver Blocklist on the same page, or another " +
               "monitoring tool already holding the hardware.";
    }

    /// <summary>
    /// Other software that takes exclusive low-level hardware access. Motherboard
    /// vendor suites are the common culprit because they start with Windows and
    /// never let go.
    /// </summary>
    private static string? ConflictingTool()
    {
        (string process, string name)[] known =
        [
            ("ArmouryCrate", "ASUS Armoury Crate"),
            ("AsusFanControlService", "ASUS Armoury Crate"),
            ("AISuite", "ASUS AI Suite"),
            ("atkexComSvc", "ASUS AI Suite"),
            ("MSIAfterburner", "MSI Afterburner"),
            ("Dragon_Center", "MSI Dragon Center"),
            ("MSI Center", "MSI Center"),
            ("aida64", "AIDA64"),
            ("HWiNFO64", "HWiNFO"),
            ("HWMonitor", "CPUID HWMonitor"),
            ("OpenHardwareMonitor", "Open Hardware Monitor"),
            ("LibreHardwareMonitor", "Libre Hardware Monitor"),
            ("GIGABYTE Control Center", "GIGABYTE Control Center"),
            ("RGBFusion", "GIGABYTE RGB Fusion"),
            ("CorsairService", "Corsair iCUE"),
            ("iCUE", "Corsair iCUE"),
            ("SignalRgb", "SignalRGB"),
            ("ThrottleStop", "ThrottleStop"),
        ];

        foreach ((string process, string name) in known)
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(process).Length > 0) return name;
            }
            catch { /* can't enumerate; try the next */ }
        }

        return null;
    }

    /// <summary>
    /// The sensor library's own diagnostic dump, including why its kernel driver
    /// did or didn't load. This is the authoritative answer when a machine reports
    /// no temperatures: far better than inferring from the outside.
    /// </summary>
    public string Report()
    {
        if (!_open) return "The sensor layer was never opened.";
        try { return _computer.GetReport(); }
        catch (Exception ex) { return "Could not produce a report: " + ex; }
    }

    public IReadOnlyList<IHardware> Hardware =>
        _open ? _computer.Hardware.ToList() : [];

    public IEnumerable<IHardware> OfType(params HardwareType[] types) =>
        Hardware.Where(h => types.Contains(h.HardwareType));

    // ---- sensor lookup helpers -------------------------------------------------

    /// <summary>All sensors on a device, including those on its sub-devices.</summary>
    public static IEnumerable<ISensor> AllSensors(IHardware hw)
    {
        foreach (ISensor s in hw.Sensors) yield return s;
        foreach (IHardware sub in hw.SubHardware)
            foreach (ISensor s in AllSensors(sub)) yield return s;
    }

    /// <summary>First sensor of <paramref name="type"/> whose name matches any of
    /// <paramref name="names"/>, tried in order so callers can express a preference.</summary>
    public static float? Value(IHardware hw, SensorType type, params string[] names)
    {
        List<ISensor> pool = AllSensors(hw).Where(s => s.SensorType == type).ToList();
        foreach (string name in names)
        {
            ISensor? hit = pool.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is float v) return v;
        }
        return null;
    }

    /// <summary>Like <see cref="Value"/> but matches on "name contains".</summary>
    public static float? ValueContaining(IHardware hw, SensorType type, params string[] fragments)
    {
        List<ISensor> pool = AllSensors(hw).Where(s => s.SensorType == type).ToList();
        foreach (string fragment in fragments)
        {
            ISensor? hit = pool.FirstOrDefault(s =>
                s.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is float v) return v;
        }
        return null;
    }

    public static IEnumerable<ISensor> Sensors(IHardware hw, SensorType type) =>
        AllSensors(hw).Where(s => s.SensorType == type);

    public void Dispose()
    {
        if (!_open) return;
        try { _computer.Close(); } catch { /* shutting down anyway */ }
        _open = false;
    }
}
