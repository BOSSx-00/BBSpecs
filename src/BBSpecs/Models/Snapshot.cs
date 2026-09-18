namespace BBSpecs.Models;

/// <summary>
/// The complete picture of the machine, rebuilt on every tick and handed to the
/// front-end as JSON. Anything that is expensive to collect is cached upstream;
/// this type is just the shape of the payload.
/// </summary>
public sealed class Snapshot
{
    public string Version { get; set; } = "0.0.0";
    public bool IsAdmin { get; set; }
    public bool SensorsReady { get; set; }
    public string? SensorNote { get; set; }

    /// <summary>A page that fixes whatever <see cref="SensorNote"/> describes.</summary>
    public string? SensorHelpUrl { get; set; }
    public long TimestampMs { get; set; }

    public SystemInfo System { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public MemoryInfo Memory { get; set; } = new();
    public List<DriveInfo> Drives { get; set; } = [];
    public NetworkInfo Network { get; set; } = new();
    public BoardInfo Board { get; set; } = new();
    public List<FanInfo> Fans { get; set; } = [];

    /// <summary>What to spend money on first, cheapest useful change at the top.</summary>
    public List<Upgrade> Upgrades { get; set; } = [];

    /// <summary>Ready-made plain text for the Copy Specs button.</summary>
    public string SpecsText { get; set; } = "";
}

/// <summary>One recommended purchase, named so it can be searched for.</summary>
public sealed class Upgrade
{
    public string Part { get; set; } = "";
    /// <summary>high | medium | low</summary>
    public string Priority { get; set; } = "low";
    public string Current { get; set; } = "";
    public string Pick { get; set; } = "";
    public string Why { get; set; } = "";
}

/// <summary>
/// A plain-language rating. <see cref="Tier"/> drives the colour in the UI,
/// <see cref="Label"/> is the words the user reads, and <see cref="Reason"/>
/// explains it in one sentence so nobody has to guess.
/// </summary>
public sealed class Verdict
{
    /// <summary>excellent | good | ok | aging | poor | unknown</summary>
    public string Tier { get; set; } = "unknown";
    public string Label { get; set; } = "Unknown";
    public string Reason { get; set; } = "";

    public static Verdict Make(string tier, string label, string reason) =>
        new() { Tier = tier, Label = label, Reason = reason };

    public static Verdict Unknown(string reason = "Not enough information to rate this.") =>
        new() { Tier = "unknown", Label = "Unknown", Reason = reason };
}

public sealed class SystemInfo
{
    public string ComputerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string OsName { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string? OsInstalledOn { get; set; }
    public double UptimeHours { get; set; }
    public Verdict Overall { get; set; } = Verdict.Unknown();
    public int Score { get; set; }
    public List<string> Highlights { get; set; } = [];
    public List<string> Watchouts { get; set; } = [];
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Socket { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public double BaseClockGhz { get; set; }
    public double? CurrentClockGhz { get; set; }
    public double? MaxClockGhz { get; set; }
    public double? LoadPercent { get; set; }
    public double? TempC { get; set; }
    public double? TempMaxC { get; set; }
    public double? PowerW { get; set; }
    public int? L2CacheKb { get; set; }
    public int? L3CacheKb { get; set; }
    public int? ReleaseYear { get; set; }
    public string? Family { get; set; }
    public List<CoreInfo> Cores { get; set; } = [];
    public Verdict Verdict { get; set; } = Verdict.Unknown();
    public Verdict Thermal { get; set; } = Verdict.Unknown();
}

public sealed class CoreInfo
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public double? LoadPercent { get; set; }
    public double? ClockGhz { get; set; }
    public double? TempC { get; set; }
    /// <summary>good | warm | hot: how this individual core is doing.</summary>
    public string Health { get; set; } = "unknown";
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string? DriverVersionFriendly { get; set; }
    public string? DriverDate { get; set; }
    public double? VramTotalGb { get; set; }
    public double? VramUsedGb { get; set; }
    public double? VramPercent { get; set; }
    public double? LoadPercent { get; set; }
    public double? TempC { get; set; }
    public double? HotspotTempC { get; set; }
    public double? CoreClockMhz { get; set; }
    public double? MemoryClockMhz { get; set; }
    public double? FanPercent { get; set; }
    public double? FanRpm { get; set; }
    public double? PowerW { get; set; }
    public string? Location { get; set; }
    public string? Resolution { get; set; }
    public double? RefreshHz { get; set; }
    public bool IsIntegrated { get; set; }
    public int? ReleaseYear { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Unknown();
    public Verdict Thermal { get; set; } = Verdict.Unknown();
}

public sealed class MemoryInfo
{
    public double TotalGb { get; set; }
    public double? UsedGb { get; set; }
    public double? AvailableGb { get; set; }
    public double? LoadPercent { get; set; }
    public string? Type { get; set; }
    public int? SpeedMhz { get; set; }
    public int SlotsUsed { get; set; }
    public int SlotsTotal { get; set; }
    public List<MemoryStick> Sticks { get; set; } = [];
    public Verdict Verdict { get; set; } = Verdict.Unknown();
}

public sealed class MemoryStick
{
    public string Slot { get; set; } = "";
    public string? Bank { get; set; }
    public double CapacityGb { get; set; }
    public int? SpeedMhz { get; set; }
    public int? ConfiguredSpeedMhz { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? Type { get; set; }
    public string? FormFactor { get; set; }
    public string? Serial { get; set; }
}

public sealed class DriveInfo
{
    public int Index { get; set; }
    public string Model { get; set; } = "";
    public string? Serial { get; set; }
    public string Kind { get; set; } = "";          // NVMe SSD / SATA SSD / Hard Drive / USB Drive
    public string? BusType { get; set; }
    public string? Firmware { get; set; }
    public double SizeGb { get; set; }
    public double? TempC { get; set; }
    public double? HealthPercent { get; set; }
    public string? HealthStatus { get; set; }
    public double? DataWrittenTb { get; set; }
    public double? DataReadTb { get; set; }
    public double? ActivityPercent { get; set; }
    public int? PowerOnHours { get; set; }
    public bool IsSystemDrive { get; set; }
    public bool IsRemovable { get; set; }
    public List<VolumeInfo> Volumes { get; set; } = [];
    public Verdict Verdict { get; set; } = Verdict.Unknown();
    public Verdict Space { get; set; } = Verdict.Unknown();
}

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedGb { get; set; }
    public double UsedPercent { get; set; }
    public bool IsSystem { get; set; }
}

public sealed class NetworkInfo
{
    /// <summary>Wi-Fi | Ethernet | Mobile | Offline</summary>
    public string ConnectionType { get; set; } = "Offline";
    public bool Online { get; set; }
    public int? PingMs { get; set; }
    public AdapterInfo? Primary { get; set; }
    public WifiInfo? Wifi { get; set; }
    public VpnInfo Vpn { get; set; } = new();
    public List<AdapterInfo> Adapters { get; set; } = [];
    public double? DownloadKbps { get; set; }
    public double? UploadKbps { get; set; }
    public PublicIpInfo? PublicIp { get; set; }

    /// <summary>A lookup is in flight, usually because the connection just changed.</summary>
    public bool PublicIpRefreshing { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Unknown();
}

public sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "";         // Wi-Fi / Ethernet / Virtual / ...
    public string? Mac { get; set; }
    public string? Ipv4 { get; set; }
    public string? Ipv6 { get; set; }
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public List<string> Dns { get; set; } = [];
    public double? LinkSpeedMbps { get; set; }
    public bool DhcpEnabled { get; set; }
    public bool IsUp { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsVirtual { get; set; }
}

public sealed class WifiInfo
{
    public string Ssid { get; set; } = "";
    public string? Bssid { get; set; }
    public int SignalPercent { get; set; }
    public string SignalLabel { get; set; } = "";
    public string? RadioType { get; set; }          // "Wi-Fi 6 (802.11ax)"
    public string? Band { get; set; }               // "5 GHz"
    public int? Channel { get; set; }
    public string? Security { get; set; }
    public string? Cipher { get; set; }
    public double? ReceiveMbps { get; set; }
    public double? TransmitMbps { get; set; }
    public string? Profile { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Unknown();
}

public sealed class VpnInfo
{
    public bool Active { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string Summary { get; set; } = "No VPN detected";
}

public sealed class PublicIpInfo
{
    public string? Ip { get; set; }
    public string? Isp { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public string? Error { get; set; }
}

public sealed class BoardInfo
{
    public string Manufacturer { get; set; } = "";
    public string Product { get; set; } = "";
    public string? Version { get; set; }
    public string? Serial { get; set; }
    public string? Chipset { get; set; }
    public string? BiosVendor { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosDate { get; set; }
    public double? TempC { get; set; }
}

public sealed class FanInfo
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Rpm { get; set; }
    public double? Percent { get; set; }
}
