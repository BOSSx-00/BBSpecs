using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BBSpecs.Models;
using BBSpecs.Services;
using DriveInfo = BBSpecs.Models.DriveInfo;

namespace BBSpecs.Platform.Linux;

/// <summary>
/// Linux readings. The kernel publishes nearly everything through /proc and /sys,
/// which needs no extra software; a few extras (RAM slot layout, drive health,
/// NVIDIA details) come from standard tools when they happen to be installed.
/// Nothing here is required: a machine missing every optional tool still shows
/// its processor, memory, drives and network.
/// </summary>
public sealed partial class LinuxHardware : SnapshotProvider
{
    [GeneratedRegex(@"^(loop|ram|zram|dm-|sr|md|fd)")]
    private static partial Regex PseudoBlockRx();

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketedNameRx();

    [GeneratedRegex(@"p?\d+$")]
    private static partial Regex PartitionSuffixRx();

    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    private readonly List<HwmonChip> _chips = [];
    private Dictionary<string, (long Idle, long Total)> _cpuTimes = [];
    private (double Microjoules, long Ticks)? _energy;
    private string? _note;

    public LinuxHardware() : base(new LinuxNetwork()) { }

    /// <summary>
    /// Temperatures come from hwmon, which any user can read. Root only adds the
    /// RAM slot layout and drive health, so sensors count as ready whenever at
    /// least one chip turned up.
    /// </summary>
    public override bool SensorsReady => _chips.Count > 0;

    public override string? SensorNote => _note;

    public override void Start() => RefreshSensors();

    protected override void RefreshSensors()
    {
        _chips.Clear();
        _chips.AddRange(Hwmon.Chips());

        _note = _chips.Count == 0
            ? "No hwmon sensors were found. On most distributions `sudo modprobe coretemp` " +
              "(Intel) or `k10temp` (AMD) makes them appear; `lm-sensors` sets this up permanently."
            : null;
    }

    protected override double ReadUptimeHours()
    {
        // /proc/uptime's first field is seconds since boot, as a decimal.
        string? raw = Sys.Text("/proc/uptime");
        string? first = raw?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return double.TryParse(first, NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds)
            ? seconds / 3600.0
            : base.ReadUptimeHours();
    }

    // ============================================================ SYSTEM ====

    protected override SystemInfo ReadSystem()
    {
        var info = new SystemInfo
        {
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            OsArchitecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            Manufacturer = Dmi("sys_vendor") ?? "",
            Model = Dmi("product_name") ?? "",
        };

        // /etc/os-release is the one file every modern distribution agrees on.
        Dictionary<string, string> release = Sys.KeyValues("/etc/os-release", '=');
        info.OsName = Unquote(release.GetValueOrDefault("PRETTY_NAME"))
                      ?? Unquote(release.GetValueOrDefault("NAME"))
                      ?? "Linux";

        string? kernel = Sys.Text("/proc/sys/kernel/osrelease");
        info.OsBuild = kernel is null ? "" : "kernel " + kernel;

        return info;
    }

    private static string? Dmi(string field) => Sys.Text("/sys/class/dmi/id/" + field);

    private static string? Unquote(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');

    // =============================================================== CPU ====

    protected override CpuInfo ReadCpu()
    {
        var cpu = new CpuInfo();
        List<Dictionary<string, string>> processors = ReadCpuInfo();

        if (processors.Count > 0)
        {
            Dictionary<string, string> first = processors[0];
            cpu.Name = CpuNames.Clean(first.GetValueOrDefault("model name")
                                      ?? first.GetValueOrDefault("Model")
                                      ?? "Unknown processor");
            string vendor = first.GetValueOrDefault("vendor_id") ?? "";
            cpu.Vendor = vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                       : vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                       : vendor;

            cpu.LogicalProcessors = processors.Count;

            // Physical cores are the distinct (package, core) pairs. Some kernels
            // and every ARM board omit those fields, so fall back to "cpu cores".
            HashSet<string> physical = processors
                .Where(p => p.ContainsKey("core id") && p.ContainsKey("physical id"))
                .Select(p => p["physical id"] + ":" + p["core id"])
                .ToHashSet();

            cpu.PhysicalCores = physical.Count > 0
                ? physical.Count
                : int.TryParse(first.GetValueOrDefault("cpu cores"), out int declared) && declared > 0
                    ? declared
                    : processors.Count;
        }

        if (cpu.LogicalProcessors == 0) cpu.LogicalProcessors = Environment.ProcessorCount;
        if (cpu.PhysicalCores == 0) cpu.PhysicalCores = Environment.ProcessorCount;

        // base_frequency is in kHz and only exists on intel_pstate; otherwise take
        // the ceiling the governor will allow, then the "@ 3.50GHz" in the model.
        double? baseKhz = Sys.Number("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency")
                          ?? Sys.Number("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        cpu.BaseClockGhz = baseKhz is double khz ? Math.Round(khz / 1_000_000.0, 2) : ClockFromName(cpu.Name);

        (cpu.L2CacheKb, cpu.L3CacheKb) = ReadCacheSizes();

        (int? year, string? family, int tier) = Verdicts.ReadCpuModel(cpu.Name);
        cpu.ReleaseYear = year;
        cpu.Family = family;
        cpu.ShortName = CpuNames.Shorten(cpu.Name);
        cpu.Verdict = Verdicts.RateCpu(cpu.Name, cpu.PhysicalCores, cpu.LogicalProcessors, year, tier);
        return cpu;
    }

    /// <summary>/proc/cpuinfo is a run of blank-line-separated blocks, one per logical CPU.</summary>
    private static List<Dictionary<string, string>> ReadCpuInfo()
    {
        var blocks = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in Sys.Lines("/proc/cpuinfo"))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { blocks.Add(current); current = new(StringComparer.OrdinalIgnoreCase); }
                continue;
            }

            int at = line.IndexOf(':');
            if (at <= 0) continue;
            current[line[..at].Trim()] = line[(at + 1)..].Trim();
        }

        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    private static double ClockFromName(string name)
    {
        Match m = Regex.Match(name, @"@\s*([\d.]+)\s*GHz", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any,
            CultureInfo.InvariantCulture, out double ghz) ? ghz : 0;
    }

    private static (int? L2, int? L3) ReadCacheSizes()
    {
        int? l2 = null, l3 = null;

        foreach (string dir in Sys.Directories("/sys/devices/system/cpu/cpu0/cache", "index*"))
        {
            long? level = Sys.Integer(Path.Combine(dir, "level"));
            string? size = Sys.Text(Path.Combine(dir, "size"));   // e.g. "512K" or "16384K"
            if (level is null || size is null) continue;

            int kb = ParseSizeKb(size);
            if (kb <= 0) continue;

            if (level == 2) l2 = kb;
            else if (level == 3) l3 = kb;
        }

        return (l2, l3);
    }

    private static int ParseSizeKb(string text)
    {
        Match m = Regex.Match(text.Trim(), @"^(\d+)\s*([KMG])?", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int value)) return 0;

        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "M" => value * 1024,
            "G" => value * 1024 * 1024,
            _ => value,
        };
    }

    protected override void ApplyCpuLive(CpuInfo cpu)
    {
        Dictionary<string, double> load = ReadCpuLoad();
        cpu.LoadPercent = load.GetValueOrDefault("cpu", double.NaN) is var total && !double.IsNaN(total)
            ? total : null;

        // coretemp (Intel) and k10temp / zenpower (AMD) both live in hwmon.
        HwmonChip? cpuChip = Hwmon.Named(_chips, "coretemp", "k10temp", "zenpower", "cpu_thermal").FirstOrDefault();
        if (cpuChip is not null)
        {
            cpu.TempC = Hwmon.TemperatureLike(cpuChip, "Package id 0", "Tctl", "Tdie", "Package", "CPU");
            if (cpu.TempC is null)
            {
                List<(string Label, double Celsius)> all = Hwmon.Temperatures(cpuChip);
                if (all.Count > 0) cpu.TempC = all.Max(t => t.Celsius);
            }
        }

        // acpitz is a coarse fallback, but better than showing nothing at all.
        cpu.TempC ??= Hwmon.Named(_chips, "acpitz").Select(c => Hwmon.TemperatureLike(c, "temp1"))
            .FirstOrDefault(t => t is not null);

        cpu.Cores = BuildCores(cpuChip, load);

        List<CoreInfo> clocked = cpu.Cores.Where(c => c.ClockGhz > 0).ToList();
        cpu.CurrentClockGhz = clocked.Count > 0 ? Math.Round(clocked.Average(c => c.ClockGhz!.Value), 2) : null;
        cpu.MaxClockGhz = clocked.Count > 0 ? clocked.Max(c => c.ClockGhz!.Value) : null;

        List<CoreInfo> warmed = cpu.Cores.Where(c => c.TempC.HasValue).ToList();
        cpu.TempMaxC = warmed.Count > 0 ? warmed.Max(c => c.TempC!.Value) : cpu.TempC;

        cpu.PowerW = ReadPackagePower();
        cpu.Thermal = Verdicts.RateCpuTemp(cpu.TempC);
    }

    /// <summary>
    /// One entry per physical core: load averaged across its threads, clock taken
    /// as the fastest of them, temperature matched by the coretemp "Core N" label.
    /// </summary>
    private static List<CoreInfo> BuildCores(HwmonChip? chip, Dictionary<string, double> load)
    {
        List<Dictionary<string, string>> processors = ReadCpuInfo();
        if (processors.Count == 0) return [];

        List<(string Label, double Celsius)> temps = chip is null ? [] : Hwmon.Temperatures(chip);

        // Group logical CPUs by their physical core; without those fields every
        // logical CPU stands alone, which is the right answer for such kernels.
        var groups = new Dictionary<int, List<int>>();
        foreach (Dictionary<string, string> p in processors)
        {
            if (!int.TryParse(p.GetValueOrDefault("processor"), out int logical)) continue;
            int coreId = int.TryParse(p.GetValueOrDefault("core id"), out int c) ? c : logical;

            if (!groups.TryGetValue(coreId, out List<int>? members)) groups[coreId] = members = [];
            members.Add(logical);
        }

        var cores = new List<CoreInfo>();
        foreach ((int coreId, List<int> members) in groups.OrderBy(g => g.Key))
        {
            List<double> loads = members
                .Select(m => load.GetValueOrDefault("cpu" + m, double.NaN))
                .Where(v => !double.IsNaN(v))
                .ToList();

            List<double> clocks = members
                .Select(m => Sys.Number($"/sys/devices/system/cpu/cpu{m}/cpufreq/scaling_cur_freq"))
                .Where(v => v is > 0)
                .Select(v => v!.Value / 1_000_000.0)
                .ToList();

            double? temp = null;
            foreach ((string label, double celsius) in temps)
            {
                if (label.Equals($"Core {coreId}", StringComparison.OrdinalIgnoreCase)) { temp = celsius; break; }
            }

            double? corLoad = loads.Count > 0 ? Math.Round(loads.Average(), 1) : null;

            cores.Add(new CoreInfo
            {
                Index = coreId,
                Label = $"Core {coreId}",
                LoadPercent = corLoad,
                ClockGhz = clocks.Count > 0 ? Math.Round(clocks.Max(), 2) : null,
                TempC = temp,
                Health = Verdicts.CoreHealth(temp, corLoad),
            });
        }

        return cores;
    }

    /// <summary>
    /// Busy percentage per CPU, from the jiffy counters in /proc/stat. These are
    /// totals since boot, so the figure is the change since the previous tick.
    /// </summary>
    private Dictionary<string, double> ReadCpuLoad()
    {
        var current = new Dictionary<string, (long Idle, long Total)>();
        var percentages = new Dictionary<string, double>();

        foreach (string line in Sys.Lines("/proc/stat"))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            long[] values = parts.Skip(1)
                .Select(p => long.TryParse(p, out long v) ? v : 0)
                .ToArray();

            // user nice system idle iowait irq softirq steal …
            long idle = values[3] + (values.Length > 4 ? values[4] : 0);
            long total = values.Sum();
            current[parts[0]] = (idle, total);
        }

        foreach ((string key, (long Idle, long Total) now) in current)
        {
            if (!_cpuTimes.TryGetValue(key, out (long Idle, long Total) before)) continue;

            long deltaTotal = now.Total - before.Total;
            long deltaIdle = now.Idle - before.Idle;
            if (deltaTotal <= 0) continue;

            double busy = (deltaTotal - deltaIdle) / (double)deltaTotal * 100.0;
            percentages[key] = Math.Clamp(Math.Round(busy, 1), 0, 100);
        }

        _cpuTimes = current;
        return percentages;
    }

    /// <summary>
    /// Package power from the RAPL energy counter: microjoules since boot, so
    /// watts is the change divided by the elapsed time.
    /// </summary>
    private double? ReadPackagePower()
    {
        string? zone = Sys.Directories("/sys/class/powercap", "intel-rapl:*")
            .FirstOrDefault(d => Sys.Text(Path.Combine(d, "name"))?.StartsWith("package", StringComparison.OrdinalIgnoreCase) == true);
        if (zone is null) return null;

        double? microjoules = Sys.Number(Path.Combine(zone, "energy_uj"));
        if (microjoules is null) return null;

        long now = Stopwatch.GetTimestamp();
        (double Microjoules, long Ticks)? previous = _energy;
        _energy = (microjoules.Value, now);

        if (previous is null) return null;

        double seconds = (now - previous.Value.Ticks) / (double)Stopwatch.Frequency;
        double delta = microjoules.Value - previous.Value.Microjoules;

        // The counter wraps; a negative delta means it did, so skip that sample.
        if (seconds <= 0.2 || delta < 0) return null;

        return Math.Round(delta / 1_000_000.0 / seconds, 1);
    }

    // =============================================================== GPU ====

    protected override List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();

        foreach (string device in Sys.Directories("/sys/bus/pci/devices"))
        {
            // PCI class 0x0300 is a VGA controller, 0x0302 a 3D controller.
            string? pciClass = Sys.Text(Path.Combine(device, "class"));
            if (pciClass is null) continue;
            if (!pciClass.StartsWith("0x0300", StringComparison.OrdinalIgnoreCase)
                && !pciClass.StartsWith("0x0302", StringComparison.OrdinalIgnoreCase)) continue;

            string address = Path.GetFileName(device);
            string vendorId = (Sys.Text(Path.Combine(device, "vendor")) ?? "").ToLowerInvariant();

            string vendor = vendorId switch
            {
                "0x10de" => "NVIDIA",
                "0x1002" or "0x1022" => "AMD",
                "0x8086" => "Intel",
                _ => "Unknown",
            };

            string? driver = Sys.LinkName(Path.Combine(device, "driver"));
            string name = DescribeGpu(address, vendor);

            var gpu = new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                Location = FormatPciAddress(address),
                DriverVersion = driver ?? "",
                IsIntegrated = Verdicts.IsIntegrated(name),
            };

            (int? year, bool integrated) = Verdicts.ReadGpuModel(name);
            gpu.ReleaseYear = year;
            gpu.IsIntegrated = integrated || gpu.IsIntegrated;
            gpu.Verdict = Verdicts.RateGpu(name, null, year, gpu.IsIntegrated);

            list.Add(gpu);
        }

        return list.OrderBy(g => g.IsIntegrated).ToList();
    }

    /// <summary>
    /// lspci knows the marketing name ("GA104 [GeForce RTX 3070]"); without it we
    /// fall back to the vendor plus the raw PCI id, which is still something.
    /// </summary>
    private static string DescribeGpu(string address, string vendor)
    {
        string? line = Shell.Run("lspci", $"-mm -s {address}")?.Trim();
        if (!string.IsNullOrEmpty(line))
        {
            List<string> fields = SplitQuoted(line);
            if (fields.Count >= 4)
            {
                string device = fields[3];
                // "GA104 [GeForce RTX 3070]": the bracketed part is the retail name.
                Match bracketed = BracketedNameRx().Match(device);
                string model = bracketed.Success ? bracketed.Groups[1].Value : device;

                string maker = fields[2]
                    .Replace(" Corporation", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Advanced Micro Devices, Inc.", "AMD", StringComparison.OrdinalIgnoreCase)
                    .Replace(" [AMD/ATI]", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

                return model.Contains(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}";
            }
        }

        string? deviceId = Sys.Text($"/sys/bus/pci/devices/{address}/device");
        return $"{vendor} graphics ({deviceId ?? address})";
    }

    /// <summary>lspci -mm emits shell-style quoted fields.</summary>
    private static List<string> SplitQuoted(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; if (!inQuotes) { fields.Add(current.ToString()); current.Clear(); } }
            else if (inQuotes) current.Append(c);
        }

        return fields;
    }

    /// <summary>"0000:01:00.0" reads better as "PCI bus 1, device 0, function 0".</summary>
    private static string? FormatPciAddress(string address)
    {
        Match m = Regex.Match(address, @"^[0-9a-f]{4}:([0-9a-f]{2}):([0-9a-f]{2})\.(\d)$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return address;

        return $"PCI bus {Convert.ToInt32(m.Groups[1].Value, 16)}, " +
               $"device {Convert.ToInt32(m.Groups[2].Value, 16)}, " +
               $"function {m.Groups[3].Value}";
    }

    protected override void ApplyGpuLive(List<GpuInfo> gpus)
    {
        List<NvidiaReading> nvidia = ReadNvidia();

        foreach (GpuInfo gpu in gpus)
        {
            if (gpu.Vendor == "NVIDIA" && nvidia.Count > 0)
            {
                NvidiaReading r = nvidia.FirstOrDefault(x =>
                    gpu.Location is not null && x.BusId is not null
                    && gpu.Location == FormatPciAddress(x.BusId)) ?? nvidia[0];

                if (!string.IsNullOrEmpty(r.Name)) gpu.Name = r.Name;
                if (!string.IsNullOrEmpty(r.Driver))
                {
                    gpu.DriverVersion = r.Driver;
                    gpu.DriverVersionFriendly = r.Driver;
                }

                gpu.VramTotalGb = r.VramTotalMb is double t ? Math.Round(t / 1024.0, 1) : gpu.VramTotalGb;
                gpu.VramUsedGb = r.VramUsedMb is double u ? Math.Round(u / 1024.0, 2) : null;
                gpu.TempC = r.TempC;
                gpu.LoadPercent = r.LoadPercent;
                gpu.CoreClockMhz = r.CoreClockMhz;
                gpu.MemoryClockMhz = r.MemoryClockMhz;
                gpu.FanPercent = r.FanPercent;
                gpu.PowerW = r.PowerW;
            }
            else
            {
                ApplyDrmReadings(gpu);
            }

            if (gpu.VramPercent is null && gpu.VramUsedGb is double used
                && gpu.VramTotalGb is double total && total > 0)
                gpu.VramPercent = used / total * 100.0;

            gpu.Thermal = Verdicts.RateGpuTemp(gpu.TempC);
            gpu.Verdict = Verdicts.RateGpu(gpu.Name, gpu.VramTotalGb, gpu.ReleaseYear, gpu.IsIntegrated);
        }
    }

    private sealed record NvidiaReading(
        string? Name, string? Driver, double? VramTotalMb, double? VramUsedMb, double? TempC,
        double? LoadPercent, double? CoreClockMhz, double? MemoryClockMhz, double? FanPercent,
        double? PowerW, string? BusId);

    private static List<NvidiaReading> ReadNvidia()
    {
        const string fields =
            "name,driver_version,memory.total,memory.used,temperature.gpu,utilization.gpu," +
            "clocks.sm,clocks.mem,fan.speed,power.draw,pci.bus_id";

        string[] lines = Shell.RunLines("nvidia-smi",
            $"--query-gpu={fields} --format=csv,noheader,nounits");

        var readings = new List<NvidiaReading>();

        foreach (string line in lines)
        {
            string[] c = line.Split(',', StringSplitOptions.TrimEntries);
            if (c.Length < 11) continue;

            // nvidia-smi prints "[N/A]" for anything the card doesn't report.
            double? Num(int i) =>
                double.TryParse(c[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : null;

            // pci.bus_id looks like "00000000:01:00.0"; sysfs uses four hex digits.
            string? bus = c[10];
            if (!string.IsNullOrEmpty(bus) && bus.Length > 12) bus = bus[^12..].ToLowerInvariant();

            readings.Add(new NvidiaReading(
                c[0], c[1], Num(2), Num(3), Num(4), Num(5), Num(6), Num(7), Num(8), Num(9),
                string.IsNullOrEmpty(bus) ? null : "0000:" + bus[^10..]));
        }

        return readings;
    }

    /// <summary>AMD and Intel publish live figures through the DRM node in sysfs.</summary>
    private void ApplyDrmReadings(GpuInfo gpu)
    {
        string? card = FindDrmCard(gpu.Location);
        if (card is null) return;

        string device = Path.Combine(card, "device");

        gpu.LoadPercent = Sys.Number(Path.Combine(device, "gpu_busy_percent"));

        double? totalBytes = Sys.Number(Path.Combine(device, "mem_info_vram_total"));
        double? usedBytes = Sys.Number(Path.Combine(device, "mem_info_vram_used"));
        if (totalBytes is > 0) gpu.VramTotalGb = Math.Round(totalBytes.Value / Gb, 1);
        if (usedBytes is >= 0) gpu.VramUsedGb = Math.Round(usedBytes.Value / Gb, 2);

        foreach (string hwmonDir in Sys.Directories(Path.Combine(device, "hwmon"), "hwmon*"))
        {
            var chip = new HwmonChip(hwmonDir, Sys.Text(Path.Combine(hwmonDir, "name")) ?? "gpu");

            gpu.TempC ??= Hwmon.TemperatureLike(chip, "edge", "temp1") ?? Hwmon.Temperatures(chip).FirstOrDefault().Celsius;
            gpu.HotspotTempC ??= Hwmon.TemperatureLike(chip, "junction", "hotspot");
            gpu.PowerW ??= Hwmon.PowerWatts(chip);

            List<(string Label, double Rpm, double? Percent)> fans = Hwmon.Fans(chip);
            if (fans.Count > 0)
            {
                gpu.FanRpm ??= fans[0].Rpm;
                gpu.FanPercent ??= fans[0].Percent;
            }

            // freq1_input is the shader clock in Hz.
            double? shaderHz = Sys.Number(Path.Combine(hwmonDir, "freq1_input"));
            if (shaderHz is > 0) gpu.CoreClockMhz ??= Math.Round(shaderHz.Value / 1_000_000.0);
        }
    }

    /// <summary>Maps a GPU back to its /sys/class/drm/cardN node via the PCI address.</summary>
    private static string? FindDrmCard(string? location)
    {
        foreach (string card in Sys.Directories("/sys/class/drm", "card?"))
        {
            string? target = Sys.LinkTarget(Path.Combine(card, "device"));
            if (target is null) continue;

            string address = Path.GetFileName(target.TrimEnd('/'));
            if (location is null || FormatPciAddress(address) == location) return card;
        }

        return null;
    }

    // ============================================================ MEMORY ====

    protected override MemoryInfo ReadMemory()
    {
        var mem = new MemoryInfo();

        Dictionary<string, string> info = Sys.KeyValues("/proc/meminfo");
        double totalGb = MemKb(info, "MemTotal") / 1024.0 / 1024.0;

        ReadMemorySticks(mem);

        double fromSticks = mem.Sticks.Sum(s => s.CapacityGb);
        // The kernel's MemTotal excludes firmware-reserved memory, so the sum of
        // the sticks is the number that matches the box the RAM came in.
        mem.TotalGb = Math.Round(fromSticks > 0 ? fromSticks : totalGb, 1);

        mem.SlotsUsed = mem.Sticks.Count;
        if (mem.SlotsTotal < mem.SlotsUsed) mem.SlotsTotal = mem.SlotsUsed;

        mem.Type = mem.Sticks.Select(s => s.Type).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        mem.SpeedMhz = mem.Sticks
            .Select(s => s.ConfiguredSpeedMhz ?? s.SpeedMhz)
            .FirstOrDefault(s => s is > 0);

        mem.Verdict = Verdicts.RateMemory(mem.TotalGb, mem.SpeedMhz, mem.Type);
        return mem;
    }

    private static double MemKb(Dictionary<string, string> info, string key)
    {
        // Values look like "32612345 kB".
        string? raw = info.GetValueOrDefault(key)?.Split(' ').FirstOrDefault();
        return double.TryParse(raw, out double kb) ? kb : 0;
    }

    /// <summary>
    /// Per-slot detail comes from the firmware's DMI tables, which only root can
    /// read. Without it the Overview still shows the total, just not the layout.
    /// </summary>
    private void ReadMemorySticks(MemoryInfo mem)
    {
        string? output = Shell.Run(Shell.Resolve("dmidecode"), "-t 17", 6000);
        if (output is null)
        {
            mem.SlotsTotal = 0;
            return;
        }

        int slots = 0;

        foreach (string block in output.Split("Memory Device", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            Dictionary<string, string> fields = ParseDmiBlock(block);
            if (fields.Count == 0) continue;

            slots++;

            string size = fields.GetValueOrDefault("Size") ?? "";
            if (size.Contains("No Module", StringComparison.OrdinalIgnoreCase)) continue;

            double gb = ParseDmiSizeGb(size);
            if (gb <= 0) continue;

            mem.Sticks.Add(new MemoryStick
            {
                Slot = fields.GetValueOrDefault("Locator") ?? "Slot",
                Bank = Clean(fields.GetValueOrDefault("Bank Locator")),
                CapacityGb = gb,
                SpeedMhz = ParseDmiSpeed(fields.GetValueOrDefault("Speed")),
                ConfiguredSpeedMhz = ParseDmiSpeed(fields.GetValueOrDefault("Configured Memory Speed")
                                                   ?? fields.GetValueOrDefault("Configured Clock Speed")),
                Manufacturer = Clean(fields.GetValueOrDefault("Manufacturer")),
                PartNumber = Clean(fields.GetValueOrDefault("Part Number")),
                Type = Clean(fields.GetValueOrDefault("Type")),
                FormFactor = FormFactorLabel(fields.GetValueOrDefault("Form Factor")),
                Serial = Clean(fields.GetValueOrDefault("Serial Number")),
            });
        }

        mem.SlotsTotal = slots;
    }

    private static Dictionary<string, string> ParseDmiBlock(string block)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in block.Split('\n'))
        {
            // Fields are tab-indented; a non-indented line starts the next record.
            if (line.Length == 0 || (!line.StartsWith('\t') && !line.StartsWith("  ", StringComparison.Ordinal))) continue;

            int at = line.IndexOf(':');
            if (at <= 0) continue;

            string key = line[..at].Trim();
            string value = line[(at + 1)..].Trim();
            if (key.Length > 0 && !fields.ContainsKey(key)) fields[key] = value;
        }

        return fields;
    }

    /// <summary>dmidecode writes placeholders such as "Unknown" and "Not Specified".</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();

        return v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
            || v.Equals("None", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Not Provided", StringComparison.OrdinalIgnoreCase)
            || v.All(c => c == '0' || c == ' ')
            ? null : v;
    }

    private static double ParseDmiSizeGb(string size)
    {
        Match m = Regex.Match(size, @"(\d+)\s*(MB|GB|TB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, out double value)) return 0;

        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "MB" => Math.Round(value / 1024.0, 1),
            "TB" => value * 1024,
            _ => value,
        };
    }

    private static int? ParseDmiSpeed(string? speed)
    {
        if (speed is null) return null;
        Match m = Regex.Match(speed, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int mhz) && mhz > 0 ? mhz : null;
    }

    private static string? FormFactorLabel(string? formFactor) => formFactor?.Trim().ToUpperInvariant() switch
    {
        "DIMM" => "DIMM (desktop)",
        "SODIMM" => "SODIMM (laptop)",
        null => null,
        var other => Clean(other),
    };

    protected override void ApplyMemoryLive(MemoryInfo mem)
    {
        Dictionary<string, string> info = Sys.KeyValues("/proc/meminfo");

        double totalGb = MemKb(info, "MemTotal") / 1024.0 / 1024.0;
        if (totalGb <= 0) return;

        // MemAvailable is the kernel's own estimate of what a new process could
        // claim, which is a far better "free" than MemFree.
        double availableGb = MemKb(info, "MemAvailable") / 1024.0 / 1024.0;
        if (availableGb <= 0) availableGb = MemKb(info, "MemFree") / 1024.0 / 1024.0;

        mem.AvailableGb = Math.Round(availableGb, 1);
        mem.UsedGb = Math.Round(totalGb - availableGb, 1);
        mem.LoadPercent = Math.Round((totalGb - availableGb) / totalGb * 100.0, 1);
    }

    // ============================================================ DRIVES ====

    protected override List<DriveInfo> ReadDrives()
    {
        var drives = new List<DriveInfo>();
        Dictionary<string, List<string>> mounts = ReadMounts();
        int index = 0;

        foreach (string blockPath in Sys.Directories("/sys/block").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(blockPath);
            if (PseudoBlockRx().IsMatch(name)) continue;

            long? sectors = Sys.Integer(Path.Combine(blockPath, "size"));
            if (sectors is null or <= 0) continue;

            string devicePath = Path.Combine(blockPath, "device");
            bool rotational = Sys.Integer(Path.Combine(blockPath, "queue/rotational")) == 1;
            bool removable = Sys.Integer(Path.Combine(blockPath, "removable")) == 1;

            string? vendor = Clean(Sys.Text(Path.Combine(devicePath, "vendor")));
            string? model = Clean(Sys.Text(Path.Combine(devicePath, "model")));
            string bus = BusFor(name, blockPath);

            var drive = new DriveInfo
            {
                Index = index++,
                Model = string.Join(' ', new[] { vendor, model }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } m
                    ? m : name,
                Serial = Clean(Sys.Text(Path.Combine(devicePath, "serial"))) ?? ReadSerialViaLsblk(name),
                Firmware = Clean(Sys.Text(Path.Combine(devicePath, "firmware_rev"))
                                 ?? Sys.Text(Path.Combine(devicePath, "rev"))),
                // sysfs always reports size in 512-byte sectors, whatever the real block size.
                SizeGb = Math.Round(sectors.Value * 512.0 / Gb, 1),
                BusType = bus,
                IsRemovable = removable || bus == "USB",
                Kind = KindFor(bus, rotational, removable),
            };

            drive.Volumes = Volumes.BuildFromMounts(MountsFor(name, blockPath, mounts));
            drive.IsSystemDrive = drive.Volumes.Any(v => v.IsSystem);
            drive.Verdict = Verdicts.RateDrive(drive.Kind, null, drive.IsSystemDrive);
            drive.Space = Volumes.RateTotal(drive.Volumes);

            drives.Add(drive);
        }

        return drives.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.Index).ToList();
    }

    private static string BusFor(string name, string blockPath)
    {
        if (name.StartsWith("nvme", StringComparison.Ordinal)) return "NVMe";
        if (name.StartsWith("mmcblk", StringComparison.Ordinal)) return "SD / eMMC";

        // The resolved device path runs through the controller, so a USB enclosure
        // shows up as ".../usb1/1-2/..." somewhere along the way.
        string? target = Sys.LinkTarget(Path.Combine(blockPath, "device"));
        if (target is not null)
        {
            if (target.Contains("/usb", StringComparison.OrdinalIgnoreCase)) return "USB";
            if (target.Contains("/ata", StringComparison.OrdinalIgnoreCase)) return "SATA";
        }

        return "SATA";
    }

    private static string KindFor(string bus, bool rotational, bool removable) => bus switch
    {
        "NVMe" => "NVMe SSD",
        "USB" => "USB Drive",
        "SD / eMMC" => "Memory Card",
        _ when rotational => "Hard Drive",
        _ when removable => "Removable Drive",
        "SATA" => "SATA SSD",
        _ => "SSD",
    };

    private static string? ReadSerialViaLsblk(string name)
    {
        string? serial = Shell.Run("lsblk", $"-dno SERIAL /dev/{name}")?.Trim();
        return string.IsNullOrWhiteSpace(serial) ? null : serial;
    }

    /// <summary>Device node to the places it is mounted, from /proc/mounts.</summary>
    private static Dictionary<string, List<string>> ReadMounts()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string line in Sys.Lines("/proc/mounts"))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!parts[0].StartsWith("/dev/", StringComparison.Ordinal)) continue;

            string device = Path.GetFileName(parts[0]);
            // Mount points are escaped octal-style: a space is written as \040.
            string mount = parts[1].Replace(@"\040", " ").Replace(@"\011", "\t");

            if (!map.TryGetValue(device, out List<string>? list)) map[device] = list = [];
            if (!list.Contains(mount)) list.Add(mount);
        }

        return map;
    }

    /// <summary>Every mount point that lives on this physical drive.</summary>
    private static List<string> MountsFor(string name, string blockPath, Dictionary<string, List<string>> mounts)
    {
        var result = new List<string>();

        // The drive itself may be mounted without a partition table.
        if (mounts.TryGetValue(name, out List<string>? direct)) result.AddRange(direct);

        // Partitions appear as child directories: sda1, nvme0n1p2, …
        foreach (string partition in Sys.Directories(blockPath))
        {
            string partName = Path.GetFileName(partition);
            if (!partName.StartsWith(name, StringComparison.Ordinal)) continue;
            if (!PartitionSuffixRx().IsMatch(partName[name.Length..])) continue;

            if (mounts.TryGetValue(partName, out List<string>? mounted)) result.AddRange(mounted);
        }

        // Skip the bind-mount clutter containers and snaps leave behind.
        return result
            .Where(m => !m.StartsWith("/snap/", StringComparison.Ordinal)
                        && !m.StartsWith("/var/lib/docker", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    protected override void ApplyDriveLive(List<DriveInfo> drives)
    {
        foreach (DriveInfo drive in drives)
        {
            string name = DeviceNameFor(drive);
            drive.TempC = ReadDriveTemp(name);
            ReadSmart(drive, name);

            Volumes.RefreshSpace(drive.Volumes);
            drive.Verdict = Verdicts.RateDrive(drive.Kind, drive.HealthPercent, drive.IsSystemDrive);
            drive.Space = Volumes.RateTotal(drive.Volumes);
        }
    }

    /// <summary>Recovers the kernel device name (sda, nvme0n1) for a collected drive.</summary>
    private static string DeviceNameFor(DriveInfo drive)
    {
        foreach (string blockPath in Sys.Directories("/sys/block").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(blockPath);
            if (PseudoBlockRx().IsMatch(name)) continue;

            long? sectors = Sys.Integer(Path.Combine(blockPath, "size"));
            if (sectors is null) continue;

            if (Math.Abs(Math.Round(sectors.Value * 512.0 / Gb, 1) - drive.SizeGb) < 0.2) return name;
        }

        return "";
    }

    private double? ReadDriveTemp(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // NVMe controllers and the drivetemp module both expose a hwmon node under
        // the block device itself, which is the cheapest place to look.
        foreach (string root in new[] { $"/sys/block/{name}/device/hwmon", $"/sys/block/{name}/device/device/hwmon" })
        {
            foreach (string dir in Sys.Directories(root, "hwmon*"))
            {
                var chip = new HwmonChip(dir, Sys.Text(Path.Combine(dir, "name")) ?? "drive");
                double? composite = Hwmon.TemperatureLike(chip, "Composite", "temp1");
                if (composite is not null) return Math.Round(composite.Value, 1);

                List<(string Label, double Celsius)> all = Hwmon.Temperatures(chip);
                if (all.Count > 0) return Math.Round(all[0].Celsius, 1);
            }
        }

        // Otherwise the chip may be registered globally, named after the driver.
        foreach (HwmonChip chip in Hwmon.Named(_chips, "nvme", "drivetemp"))
        {
            string? linked = Sys.LinkTarget(Path.Combine(chip.Path, "device"));
            if (linked is not null && linked.Contains(name, StringComparison.Ordinal))
            {
                double? t = Hwmon.TemperatureLike(chip, "Composite", "temp1");
                if (t is not null) return Math.Round(t.Value, 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Drive health and lifetime writes, via smartctl's JSON output. Needs root,
    /// and the tool is optional: without it those rows simply stay blank.
    /// </summary>
    private static void ReadSmart(DriveInfo drive, string name)
    {
        if (string.IsNullOrEmpty(name) || !Elevation.IsElevated) return;

        string? json = Shell.Run(Shell.Resolve("smartctl"), $"--json=c -A -H /dev/{name}", 5000);
        if (json is null) return;

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (root.TryGetProperty("smart_status", out var status)
                && status.TryGetProperty("passed", out var passed))
                drive.HealthStatus = passed.GetBoolean() ? "Healthy" : "Failing";

            if (root.TryGetProperty("temperature", out var temperature)
                && temperature.TryGetProperty("current", out var current)
                && current.TryGetInt32(out int celsius))
                drive.TempC ??= celsius;

            if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
            {
                if (nvme.TryGetProperty("percentage_used", out var used) && used.TryGetInt32(out int spent))
                    drive.HealthPercent = Math.Clamp(100 - spent, 0, 100);

                // Data units are 1000 × 512 bytes each, per the NVMe specification.
                if (nvme.TryGetProperty("data_units_written", out var written)
                    && written.TryGetInt64(out long units))
                    drive.DataWrittenTb = Math.Round(units * 512000.0 / 1024 / Gb, 2);

                if (nvme.TryGetProperty("data_units_read", out var read) && read.TryGetInt64(out long readUnits))
                    drive.DataReadTb = Math.Round(readUnits * 512000.0 / 1024 / Gb, 2);

                if (nvme.TryGetProperty("power_on_hours", out var hours) && hours.TryGetInt32(out int onHours))
                    drive.PowerOnHours = onHours;
            }

            if (root.TryGetProperty("power_on_time", out var powerOn)
                && powerOn.TryGetProperty("hours", out var sataHours)
                && sataHours.TryGetInt32(out int h))
                drive.PowerOnHours ??= h;
        }
        catch
        {
            // smartctl emitted something we don't recognise; leave the rows blank.
        }
    }

    // ======================================================= MOTHERBOARD ====

    protected override BoardInfo ReadBoard() => new()
    {
        Manufacturer = Dmi("board_vendor") ?? Dmi("sys_vendor") ?? "",
        Product = Dmi("board_name") ?? Dmi("product_name") ?? "",
        Version = Clean(Dmi("board_version")),
        Serial = Clean(Dmi("board_serial")),
        BiosVendor = Clean(Dmi("bios_vendor")),
        BiosVersion = Clean(Dmi("bios_version")),
        BiosDate = Clean(Dmi("bios_date")),
    };

    protected override void ApplyBoardLive(BoardInfo board)
    {
        // Super-I/O chips (nct6798, it8728, …) carry the board sensors. Skip the
        // ones we already attribute to the CPU or a drive.
        foreach (HwmonChip chip in _chips)
        {
            if (chip.Name is "coretemp" or "k10temp" or "zenpower" or "nvme" or "drivetemp" or "amdgpu") continue;

            double? temp = Hwmon.TemperatureLike(chip, "SYSTIN", "System", "Motherboard", "MB", "temp2");
            if (temp is not null) { board.TempC = Math.Round(temp.Value, 1); return; }
        }

        board.TempC ??= Hwmon.Named(_chips, "acpitz")
            .Select(c => Hwmon.TemperatureLike(c, "temp1"))
            .FirstOrDefault(t => t is not null);
    }

    protected override List<FanInfo> ReadFans()
    {
        var fans = new List<FanInfo>();

        foreach (HwmonChip chip in _chips)
        {
            string source = chip.Name switch
            {
                "amdgpu" or "nouveau" or "i915" or "xe" => "Graphics card",
                _ => "Case & CPU",
            };

            foreach ((string label, double rpm, double? percent) in Hwmon.Fans(chip))
            {
                fans.Add(new FanInfo
                {
                    Name = label,
                    Source = source,
                    Rpm = Math.Round(rpm),
                    Percent = percent,
                });
            }
        }

        return fans;
    }
}
