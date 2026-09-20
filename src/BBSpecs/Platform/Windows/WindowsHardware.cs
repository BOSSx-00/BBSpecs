using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using BBSpecs.Models;
using BBSpecs.Services;
using LibreHardwareMonitor.Hardware;
using DriveInfo = BBSpecs.Models.DriveInfo;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Windows readings: WMI for the specs, LibreHardwareMonitor for the sensors,
/// and the registry for the two facts WMI reports wrongly (VRAM size and PCI
/// slot location).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHardware : SnapshotProvider
{
    [GeneratedRegex(@"^CPU Core #(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CoreSensorRx();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlnumRx();

    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    private SensorHub _sensors = new();

    public WindowsHardware() : base(new WindowsNetwork()) { }

    public override bool SensorsReady => _sensors.Ready;
    public override string? SensorNote => _sensors.Note;
    public override string? SensorHelpUrl => _sensors.HelpUrl;

    public override void Start() => _sensors.Start();
    protected override void RefreshSensors() => _sensors.Refresh();

    protected override void ReloadSensors()
    {
        // A driver installed while BBSpecs was running is only visible to a fresh
        // Computer instance, so the old one is thrown away entirely.
        try { _sensors.Dispose(); } catch { }
        _sensors = new SensorHub();
        _sensors.Start();
        _sensors.Refresh();
    }

    // ============================================================ SYSTEM ====

    protected override SystemInfo ReadSystem()
    {
        var info = new SystemInfo
        {
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            OsArchitecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
        };

        ManagementObject? cs = Wmi.QueryFirst("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        if (cs is not null)
        {
            info.Manufacturer = cs.Str("Manufacturer");
            info.Model = cs.Str("Model");
        }

        ManagementObject? os = Wmi.QueryFirst(
            "SELECT Caption, BuildNumber, InstallDate FROM Win32_OperatingSystem");
        if (os is not null)
        {
            info.OsName = os.Str("Caption").Replace("Microsoft ", "");
            (string? display, string? ubr) = Reg.WindowsRelease();
            string build = os.Str("BuildNumber");
            if (!string.IsNullOrEmpty(ubr)) build += "." + ubr;
            info.OsBuild = display is null ? build : $"{display} (build {build})";
            info.OsInstalledOn = os.Date("InstallDate")?.ToString("d MMMM yyyy");
        }

        return info;
    }

    // =============================================================== CPU ====

    protected override CpuInfo ReadCpu()
    {
        var cpu = new CpuInfo();

        ManagementObject? p = Wmi.QueryFirst(
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, " +
            "SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor");

        if (p is not null)
        {
            cpu.Name = CpuNames.Clean(p.Str("Name"));
            string maker = p.Str("Manufacturer");
            cpu.Vendor = maker.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                       : maker.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                       : maker;
            cpu.PhysicalCores = (int)p.Num("NumberOfCores");
            cpu.LogicalProcessors = (int)p.Num("NumberOfLogicalProcessors");
            cpu.BaseClockGhz = p.Num("MaxClockSpeed") / 1000.0;
            cpu.Socket = p.Str("SocketDesignation");
            cpu.L2CacheKb = (int?)p.NumOrNull("L2CacheSize");
            cpu.L3CacheKb = (int?)p.NumOrNull("L3CacheSize");
        }

        if (cpu.LogicalProcessors == 0) cpu.LogicalProcessors = Environment.ProcessorCount;
        if (cpu.PhysicalCores == 0) cpu.PhysicalCores = Environment.ProcessorCount;

        (int? year, string? family, int tier) = Verdicts.ReadCpuModel(cpu.Name);
        cpu.ReleaseYear = year;
        cpu.Family = family;
        cpu.ShortName = CpuNames.Shorten(cpu.Name);
        cpu.Verdict = Verdicts.RateCpu(cpu.Name, cpu.PhysicalCores, cpu.LogicalProcessors, year, tier);
        return cpu;
    }

    protected override void ApplyCpuLive(CpuInfo cpu)
    {
        IHardware? hw = _sensors.OfType(HardwareType.Cpu).FirstOrDefault();

        if (hw is not null)
        {
            cpu.TempC = SensorHub.Value(hw, SensorType.Temperature,
                "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "CPU Cores", "Core Average", "Core Max");

            if (cpu.TempC is null)
            {
                List<double> temps = SensorHub.Sensors(hw, SensorType.Temperature)
                    .Where(s => s.Value.HasValue)
                    .Select(s => (double)s.Value!.Value)
                    .ToList();
                if (temps.Count > 0) cpu.TempC = temps.Max();
            }

            cpu.LoadPercent = SensorHub.Value(hw, SensorType.Load, "CPU Total");
            cpu.PowerW = SensorHub.Value(hw, SensorType.Power, "CPU Package", "Package", "CPU PPT") is > 0 and var w ? w : null;

            List<ISensor> coreLoads = SensorHub.Sensors(hw, SensorType.Load)
                .Where(s => CoreSensorRx().IsMatch(s.Name)).ToList();
            List<ISensor> coreClocks = SensorHub.Sensors(hw, SensorType.Clock).ToList();
            List<ISensor> coreTemps = SensorHub.Sensors(hw, SensorType.Temperature).ToList();

            var cores = new List<CoreInfo>();
            foreach (ISensor s in coreLoads.OrderBy(x => CoreIndex(x.Name)))
            {
                int index = CoreIndex(s.Name);
                double? clock = coreClocks.FirstOrDefault(c => CoreIndex(c.Name) == index)?.Value;
                double? t = coreTemps.FirstOrDefault(x => CoreIndex(x.Name) == index)?.Value;

                cores.Add(new CoreInfo
                {
                    Index = index,
                    Label = $"Core {index}",
                    LoadPercent = s.Value,
                    ClockGhz = clock is double c ? c / 1000.0 : null,
                    TempC = t,
                    Health = Verdicts.CoreHealth(t, s.Value),
                });
            }
            cpu.Cores = cores;

            List<CoreInfo> clocked = cores.Where(c => c.ClockGhz > 0).ToList();
            cpu.CurrentClockGhz = clocked.Count > 0
                ? clocked.Average(c => c.ClockGhz!.Value)
                : SensorHub.ValueContaining(hw, SensorType.Clock, "CPU Core") is float f ? f / 1000.0 : null;
            cpu.MaxClockGhz = clocked.Count > 0 ? clocked.Max(c => c.ClockGhz!.Value) : cpu.CurrentClockGhz;

            List<CoreInfo> warmed = cores.Where(c => c.TempC.HasValue).ToList();
            cpu.TempMaxC = warmed.Count > 0 ? warmed.Max(c => c.TempC!.Value) : cpu.TempC;
        }

        // Without sensor access we can still show load: Windows tracks that itself.
        cpu.LoadPercent ??= ReadCpuLoadFallback();
        cpu.Thermal = Verdicts.RateCpuTemp(cpu.TempC);
    }

    private static int CoreIndex(string sensorName)
    {
        Match m = CoreSensorRx().Match(sensorName);
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(sensorName, @"#(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    private static double? ReadCpuLoadFallback()
    {
        ManagementObject? o = Wmi.QueryFirst(
            "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
        return o is null ? null : o.Num("PercentProcessorTime");
    }

    // =============================================================== GPU ====

    protected override List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();

        foreach (ManagementObject v in Wmi.Query(
            "SELECT Name, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID, AdapterCompatibility, " +
            "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate " +
            "FROM Win32_VideoController"))
        {
            string name = v.Str("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            string pnp = v.Str("PNPDeviceID");
            string driver = v.Str("DriverVersion");

            var gpu = new GpuInfo
            {
                Name = name,
                Vendor = GpuNames.Vendor(v.Str("AdapterCompatibility"), name),
                DriverVersion = driver,
                DriverDate = v.Date("DriverDate")?.ToString("d MMMM yyyy"),
                Location = Reg.DeviceLocation(pnp),
                IsIntegrated = Verdicts.IsIntegrated(name),
            };

            gpu.DriverVersionFriendly = GpuNames.FriendlyDriverVersion(gpu.Vendor, driver);

            // AdapterRAM is a 32-bit field, so anything at or above 4 GB is wrong;
            // the registry holds the real number.
            long? vram = Reg.VideoMemoryBytes(pnp);
            if (vram is null or 0)
            {
                long adapterRam = v.Num("AdapterRAM");
                if (adapterRam > 0) vram = adapterRam;
            }
            if (vram is > 0) gpu.VramTotalGb = Math.Round(vram.Value / Gb, 1);

            long w = v.Num("CurrentHorizontalResolution");
            long h = v.Num("CurrentVerticalResolution");
            if (w > 0 && h > 0) gpu.Resolution = $"{w} × {h}";
            long hz = v.Num("CurrentRefreshRate");
            if (hz > 0) gpu.RefreshHz = hz;

            (int? year, bool integrated) = Verdicts.ReadGpuModel(name);
            gpu.ReleaseYear = year;
            gpu.IsIntegrated = integrated || gpu.IsIntegrated;
            gpu.Verdict = Verdicts.RateGpu(name, gpu.VramTotalGb, year, gpu.IsIntegrated);

            list.Add(gpu);
        }

        // Dedicated cards first: that's the one people care about.
        return list.OrderBy(g => g.IsIntegrated).ToList();
    }

    protected override void ApplyGpuLive(List<GpuInfo> gpus)
    {
        List<IHardware> hardware = _sensors
            .OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel)
            .ToList();

        foreach (GpuInfo gpu in gpus)
        {
            IHardware? hw = BestMatch(hardware, gpu.Name);
            if (hw is null)
            {
                gpu.Thermal = Verdicts.RateGpuTemp(null);
                continue;
            }

            gpu.TempC = SensorHub.Value(hw, SensorType.Temperature, "GPU Core", "GPU Temperature", "GPU");
            gpu.HotspotTempC = SensorHub.ValueContaining(hw, SensorType.Temperature, "Hot Spot", "Hotspot", "Junction");
            gpu.LoadPercent = SensorHub.Value(hw, SensorType.Load, "GPU Core", "D3D 3D");
            gpu.CoreClockMhz = SensorHub.Value(hw, SensorType.Clock, "GPU Core");
            gpu.MemoryClockMhz = SensorHub.Value(hw, SensorType.Clock, "GPU Memory");
            gpu.FanPercent = SensorHub.ValueContaining(hw, SensorType.Control, "GPU Fan", "Fan");
            gpu.FanRpm = SensorHub.ValueContaining(hw, SensorType.Fan, "GPU Fan", "Fan");
            gpu.PowerW = SensorHub.ValueContaining(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU");

            float? usedMb = SensorHub.Value(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            float? totalMb = SensorHub.Value(hw, SensorType.SmallData, "GPU Memory Total");

            if (totalMb is > 0) gpu.VramTotalGb = Math.Round(totalMb.Value / 1024.0, 1);
            if (usedMb is >= 0) gpu.VramUsedGb = Math.Round(usedMb.Value / 1024.0, 2);

            gpu.VramPercent = SensorHub.Value(hw, SensorType.Load, "GPU Memory");
            if (gpu.VramPercent is null && gpu.VramUsedGb is double used && gpu.VramTotalGb is double total && total > 0)
                gpu.VramPercent = used / total * 100.0;

            gpu.Thermal = Verdicts.RateGpuTemp(gpu.TempC);
            // The VRAM total from the sensor library is authoritative, so re-rate.
            gpu.Verdict = Verdicts.RateGpu(gpu.Name, gpu.VramTotalGb, gpu.ReleaseYear, gpu.IsIntegrated);
        }
    }

    /// <summary>Matches a WMI device name to a sensor-library device by fuzzy name.</summary>
    private static IHardware? BestMatch(List<IHardware> pool, string name)
    {
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        string target = Normalise(name);
        IHardware? exact = pool.FirstOrDefault(h => Normalise(h.Name) == target);
        if (exact is not null) return exact;

        return pool.FirstOrDefault(h =>
        {
            string candidate = Normalise(h.Name);
            return candidate.Contains(target, StringComparison.Ordinal)
                || target.Contains(candidate, StringComparison.Ordinal);
        });
    }

    private static string Normalise(string s) => NonAlnumRx().Replace(s.ToLowerInvariant(), "");

    // ============================================================ MEMORY ====

    protected override MemoryInfo ReadMemory()
    {
        var mem = new MemoryInfo();

        foreach (ManagementObject m in Wmi.Query(
            "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, " +
            "PartNumber, SMBIOSMemoryType, FormFactor, SerialNumber FROM Win32_PhysicalMemory"))
        {
            long capacity = m.Num("Capacity");
            if (capacity <= 0) continue;

            mem.Sticks.Add(new MemoryStick
            {
                Slot = m.Str("DeviceLocator"),
                Bank = m.StrOrNull("BankLabel"),
                CapacityGb = Math.Round(capacity / Gb, 1),
                SpeedMhz = (int?)m.NumOrNull("Speed"),
                ConfiguredSpeedMhz = (int?)m.NumOrNull("ConfiguredClockSpeed"),
                Manufacturer = m.StrOrNull("Manufacturer"),
                PartNumber = m.StrOrNull("PartNumber"),
                Type = MemoryNames.Type((int)m.Num("SMBIOSMemoryType")),
                FormFactor = MemoryNames.FormFactor((int)m.Num("FormFactor")),
                Serial = m.StrOrNull("SerialNumber"),
            });
        }

        mem.SlotsUsed = mem.Sticks.Count;

        ManagementObject? array = Wmi.QueryFirst("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
        mem.SlotsTotal = array is null ? mem.SlotsUsed : (int)array.Num("MemoryDevices");
        if (mem.SlotsTotal < mem.SlotsUsed) mem.SlotsTotal = mem.SlotsUsed;

        ManagementObject? cs = Wmi.QueryFirst("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
        double reported = cs is null ? 0 : cs.Num("TotalPhysicalMemory") / Gb;
        double fromSticks = mem.Sticks.Sum(s => s.CapacityGb);
        // TotalPhysicalMemory excludes memory reserved by the firmware, so the sum
        // of the sticks is the number that matches the box the RAM came in.
        mem.TotalGb = Math.Round(fromSticks > 0 ? fromSticks : reported, 1);

        mem.Type = mem.Sticks.Select(s => s.Type).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        mem.SpeedMhz = mem.Sticks
            .Select(s => s.ConfiguredSpeedMhz ?? s.SpeedMhz)
            .FirstOrDefault(s => s is > 0);

        mem.Verdict = Verdicts.RateMemory(mem.TotalGb, mem.SpeedMhz, mem.Type);
        return mem;
    }

    protected override void ApplyMemoryLive(MemoryInfo mem)
    {
        IHardware? hw = _sensors.OfType(HardwareType.Memory).FirstOrDefault();
        if (hw is not null)
        {
            mem.LoadPercent = SensorHub.Value(hw, SensorType.Load, "Memory");
            float? used = SensorHub.Value(hw, SensorType.Data, "Memory Used");
            float? available = SensorHub.Value(hw, SensorType.Data, "Memory Available");
            if (used is > 0) mem.UsedGb = Math.Round(used.Value, 1);
            if (available is > 0) mem.AvailableGb = Math.Round(available.Value, 1);
        }

        if (mem.UsedGb is not null) return;

        ManagementObject? os = Wmi.QueryFirst(
            "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
        if (os is null) return;

        double freeGb = os.Num("FreePhysicalMemory") / 1024.0 / 1024.0;
        double totalGb = os.Num("TotalVisibleMemorySize") / 1024.0 / 1024.0;
        if (totalGb <= 0) return;

        mem.AvailableGb = Math.Round(freeGb, 1);
        mem.UsedGb = Math.Round(totalGb - freeGb, 1);
        mem.LoadPercent ??= (totalGb - freeGb) / totalGb * 100.0;
    }

    // ============================================================ DRIVES ====

    protected override List<DriveInfo> ReadDrives()
    {
        var drives = new List<DriveInfo>();
        string systemLetter = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
            .TrimEnd('\\').ToUpperInvariant();

        // MSFT_PhysicalDisk knows the media type and bus; Win32_DiskDrive knows the
        // model and index. We need both, keyed on the physical drive number.
        Dictionary<int, ManagementObject> physical = [];
        foreach (ManagementObject d in Wmi.Query(
            "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size, SpindleSpeed " +
            "FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            if (int.TryParse(d.Str("DeviceId"), out int id)) physical[id] = d;
        }

        PartitionMap partitions = ReadPartitionMap();

        foreach (ManagementObject disk in Wmi.Query(
            "SELECT Index, DeviceID, Model, SerialNumber, Size, InterfaceType, MediaType, " +
            "FirmwareRevision FROM Win32_DiskDrive"))
        {
            int index = (int)disk.Num("Index");
            physical.TryGetValue(index, out ManagementObject? phys);

            string model = disk.Str("Model");
            if (string.IsNullOrWhiteSpace(model)) model = phys?.Str("FriendlyName") ?? "Unknown drive";

            int mediaType = phys is null ? 0 : (int)phys.Num("MediaType");
            int busType = phys is null ? 0 : (int)phys.Num("BusType");
            long spindle = phys?.Num("SpindleSpeed") ?? -1;

            var drive = new DriveInfo
            {
                Index = index,
                Model = model.Trim(),
                Serial = disk.StrOrNull("SerialNumber")?.Trim(),
                Firmware = disk.StrOrNull("FirmwareRevision")?.Trim(),
                SizeGb = Math.Round(disk.Num("Size") / Gb, 1),
                BusType = BusName(busType, disk.Str("InterfaceType")),
                HealthStatus = phys is null ? null : HealthName((int)phys.Num("HealthStatus")),
                IsRemovable = busType == 7 || disk.Str("MediaType")
                    .Contains("Removable", StringComparison.OrdinalIgnoreCase),
            };

            drive.Kind = DriveKind(mediaType, busType, spindle, disk.Str("MediaType"));
            drive.PartitionedGb = partitions.PartitionedGb.TryGetValue(index, out double carved)
                ? carved
                : null;

            drive.Volumes = partitions.Letters.TryGetValue(index, out List<char>? letters)
                ? Volumes.Build(letters, systemLetter)
                : ReadVolumesViaAssociators(disk.Str("DeviceID"), systemLetter);
            drive.IsSystemDrive = drive.Volumes.Any(v => v.IsSystem);
            drive.Verdict = Verdicts.RateDrive(drive.Kind, null, drive.IsSystemDrive);
            drive.Space = Volumes.RateTotal(drive.Volumes);

            drives.Add(drive);
        }

        return drives.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.Index).ToList();
    }

    private static string DriveKind(int mediaType, int busType, long spindle, string legacyMedia)
    {
        bool ssd = mediaType == 4 || (mediaType == 0 && spindle == 0);
        bool hdd = mediaType == 3 || spindle > 0;

        return busType switch
        {
            17 => "NVMe SSD",
            7 => "USB Drive",
            _ when ssd => busType == 11 ? "SATA SSD" : "SSD",
            _ when hdd => "Hard Drive",
            _ when legacyMedia.Contains("Fixed", StringComparison.OrdinalIgnoreCase) => "Fixed Drive",
            _ => "Drive",
        };
    }

    private static string BusName(int busType, string fallback) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => string.IsNullOrWhiteSpace(fallback) ? "Unknown" : fallback,
    };

    private static string HealthName(int health) => health switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy",
        _ => "Unknown",
    };

    /// <summary>
    /// Physical drive number to the drive letters living on it. MSFT_Partition
    /// carries DiskNumber directly, which saves two association queries per drive.
    /// </summary>
    private static PartitionMap ReadPartitionMap()
    {
        var letters = new Dictionary<int, List<char>>();
        var sizes = new Dictionary<int, double>();

        foreach (ManagementObject part in Wmi.Query(
            "SELECT DiskNumber, DriveLetter, Size FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
        {
            int disk = (int)part.Num("DiskNumber");

            // Every partition counts towards the total, lettered or not. The
            // unlettered ones are exactly what makes a healthy disk look empty.
            sizes[disk] = sizes.GetValueOrDefault(disk) + part.Num("Size") / 1024d / 1024d / 1024d;

            if (PartitionLetter(part) is not char letter) continue;

            if (!letters.TryGetValue(disk, out List<char>? found)) letters[disk] = found = [];
            found.Add(letter);
        }

        return new PartitionMap(letters, sizes);
    }

    /// <summary>Drive letters and total partitioned size, both keyed by disk number.</summary>
    private sealed record PartitionMap(
        Dictionary<int, List<char>> Letters,
        Dictionary<int, double> PartitionedGb);

    /// <summary>DriveLetter comes back as a CIM char; unlettered partitions give NUL.</summary>
    private static char? PartitionLetter(ManagementBaseObject part)
    {
        try
        {
            object? value = part["DriveLetter"];
            if (value is null) return null;

            char c = value is char ch ? ch : Convert.ToChar(Convert.ToUInt16(value));
            return char.IsLetter(c) ? char.ToUpperInvariant(c) : null;
        }
        catch { return null; }
    }

    /// <summary>Fallback for machines without the Storage WMI provider.</summary>
    private static List<VolumeInfo> ReadVolumesViaAssociators(string diskDeviceId, string systemLetter)
    {
        if (string.IsNullOrEmpty(diskDeviceId)) return [];
        var letters = new List<char>();

        // The text inside the braces is an object path, so backslashes are NOT
        // doubled the way they would be in a WHERE clause: doubling them makes
        // WMI answer "Not found".
        string diskPath = Wmi.EscapePath(diskDeviceId);
        foreach (ManagementObject part in Wmi.Query(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{diskPath}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
        {
            string partPath = Wmi.EscapePath(part.Str("DeviceID"));
            foreach (ManagementObject logical in Wmi.Query(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partPath}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
            {
                string id = logical.Str("DeviceID");
                if (id.Length > 0 && char.IsLetter(id[0])) letters.Add(char.ToUpperInvariant(id[0]));
            }
        }

        return Volumes.Build(letters, systemLetter);
    }

    protected override void ApplyDriveLive(List<DriveInfo> drives)
    {
        List<IHardware> storage = _sensors.OfType(HardwareType.Storage).ToList();

        foreach (DriveInfo drive in drives)
        {
            // Sensor-library storage identifiers carry the physical drive number
            // (/nvme/0, /hdd/1), which lines up with Win32_DiskDrive.Index.
            IHardware? hw = storage.FirstOrDefault(h => IdentifierIndex(h) == drive.Index)
                            ?? BestMatch(storage, drive.Model);

            if (hw is not null)
            {
                drive.TempC = SensorHub.ValueContaining(hw, SensorType.Temperature, "Temperature");
                // Drives label this differently depending on make and bus, and a
                // few report wear used rather than life left.
                drive.HealthPercent = SensorHub.ValueContaining(hw, SensorType.Level,
                    "Remaining Life", "Percentage Used", "Life Left", "Health", "Wear");

                if (drive.HealthPercent is double wear
                    && SensorHub.Sensors(hw, SensorType.Level).Any(x =>
                        x.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase)
                        || x.Name.Contains("Wear", StringComparison.OrdinalIgnoreCase)))
                {
                    drive.HealthPercent = Math.Clamp(100 - wear, 0, 100);
                }

                float? written = SensorHub.ValueContaining(hw, SensorType.Data, "Data Written", "Total Host Writes");
                float? read = SensorHub.ValueContaining(hw, SensorType.Data, "Data Read", "Total Host Reads");
                if (written is > 0) drive.DataWrittenTb = Math.Round(written.Value / 1024.0, 2);
                if (read is > 0) drive.DataReadTb = Math.Round(read.Value / 1024.0, 2);

                // "Used Space" is also a Load sensor, so match the activity one by name.
                drive.ActivityPercent = SensorHub.ValueContaining(hw, SensorType.Load, "Total Activity");
            }

            Volumes.RefreshSpace(drive.Volumes);
            drive.Verdict = Verdicts.RateDrive(drive.Kind, drive.HealthPercent, drive.IsSystemDrive);
            drive.Space = Volumes.RateTotal(drive.Volumes);
        }
    }

    private static int IdentifierIndex(IHardware hw)
    {
        Match m = Regex.Match(hw.Identifier.ToString(), @"/(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    // ======================================================= MOTHERBOARD ====

    protected override BoardInfo ReadBoard()
    {
        var board = new BoardInfo();

        ManagementObject? b = Wmi.QueryFirst(
            "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
        if (b is not null)
        {
            board.Manufacturer = b.Str("Manufacturer");
            board.Product = b.Str("Product");
            board.Version = b.StrOrNull("Version");
            board.Serial = b.StrOrNull("SerialNumber");
        }

        ManagementObject? bios = Wmi.QueryFirst(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        if (bios is not null)
        {
            board.BiosVendor = bios.StrOrNull("Manufacturer");
            board.BiosVersion = bios.StrOrNull("SMBIOSBIOSVersion");
            board.BiosDate = bios.Date("ReleaseDate")?.ToString("d MMMM yyyy");
        }

        return board;
    }

    protected override void ApplyBoardLive(BoardInfo board)
    {
        IHardware? hw = _sensors.OfType(HardwareType.Motherboard).FirstOrDefault();
        if (hw is null) return;

        board.TempC = SensorHub.ValueContaining(hw, SensorType.Temperature,
            "System", "Motherboard", "Mainboard", "Temperature");
    }

    /// <summary>
    /// Runs on the background sweep thread, not the collector, so the WMI cost
    /// never shows up as a stutter in the live readings.
    /// </summary>
    protected override List<DriverInfo> ReadDrivers() => WindowsDrivers.Read(ReadBoard());

    protected override List<FanInfo> ReadFans()
    {
        var fans = new List<FanInfo>();

        foreach (IHardware hw in _sensors.OfType(
            HardwareType.Motherboard, HardwareType.Cooler,
            HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel))
        {
            string source = hw.HardwareType switch
            {
                HardwareType.Motherboard => "Case & CPU",
                HardwareType.Cooler => hw.Name,
                _ => "Graphics card",
            };

            List<ISensor> controls = SensorHub.Sensors(hw, SensorType.Control).ToList();

            foreach (ISensor s in SensorHub.Sensors(hw, SensorType.Fan))
            {
                if (s.Value is not float rpm || rpm <= 0) continue;

                fans.Add(new FanInfo
                {
                    Name = s.Name,
                    Source = source,
                    Rpm = Math.Round(rpm),
                    Percent = controls.FirstOrDefault(c => c.Name == s.Name)?.Value,
                });
            }
        }

        return fans;
    }

    public override void Dispose()
    {
        _sensors.Dispose();
        base.Dispose();
    }
}
