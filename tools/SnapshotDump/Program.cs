using BBSpecs.Models;
using BBSpecs.Services;

// Collects two snapshots a second apart: throughput and CPU load need a delta
// to mean anything: then prints the second and writes it out as the exact JSON
// the user interface receives.
//
// Pass --sensors to list every raw sensor instead. That is the quickest way to
// find out why a reading is blank on a particular machine: if a value isn't in
// that list, the hardware or the driver isn't offering it.

if (args.Contains("--sensors"))
{
    DumpSensors();
    return;
}

if (args.Contains("--report"))
{
    DumpDriverReport();
    return;
}

string outPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Path.Combine(AppContext.BaseDirectory, "snapshot.json");

SnapshotProvider provider =
#if BBSPECS_WINDOWS
    new BBSpecs.Platform.Windows.WindowsHardware();
#else
    new BBSpecs.Platform.Linux.LinuxHardware();
#endif

using (provider)
{
    Console.WriteLine($"Platform: {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}   " +
                      $"Elevated: {Elevation.IsElevated} ({Elevation.RoleName})");

    if (!OperatingSystem.IsWindows()) ReportOptionalTools();

    provider.Start();
    provider.Build("0.0.0-dev");
    Thread.Sleep(1200);
    Snapshot s = provider.Build("0.0.0-dev");

    Console.WriteLine($"Sensors ready: {s.SensorsReady}   {s.SensorNote}");
    Console.WriteLine();

    Console.WriteLine($"SYSTEM   {s.System.OsName}  {s.System.OsBuild}");
    Console.WriteLine($"         {s.System.Manufacturer} {s.System.Model}   up {s.System.UptimeHours:0.0} h");

    Console.WriteLine($"CPU      {s.Cpu.Name}");
    Console.WriteLine($"         {s.Cpu.PhysicalCores}C/{s.Cpu.LogicalProcessors}T   " +
                      $"temp={N(s.Cpu.TempC)}   load={N(s.Cpu.LoadPercent)}   " +
                      $"clock={N(s.Cpu.CurrentClockGhz, "0.00")}   base={s.Cpu.BaseClockGhz:0.00}   " +
                      $"power={N(s.Cpu.PowerW)}   cores reported={s.Cpu.Cores.Count}");
    Console.WriteLine($"         L2={N(s.Cpu.L2CacheKb)}kB  L3={N(s.Cpu.L3CacheKb)}kB   " +
                      $"verdict={s.Cpu.Verdict.Tier}/{s.Cpu.Verdict.Label}");

    foreach (CoreInfo c in s.Cpu.Cores.Take(4))
        Console.WriteLine($"           {c.Label}: {N(c.TempC)}°C  {N(c.ClockGhz, "0.00")}GHz  {N(c.LoadPercent)}%  [{c.Health}]");
    if (s.Cpu.Cores.Count > 4) Console.WriteLine($"           … {s.Cpu.Cores.Count - 4} more");

    foreach (GpuInfo g in s.Gpus)
        Console.WriteLine($"GPU      {g.Name} ({g.Vendor})\n" +
                          $"         vram={N(g.VramTotalGb)}GB used={N(g.VramUsedGb, "0.00")}GB  " +
                          $"temp={N(g.TempC)}  load={N(g.LoadPercent)}  fan={N(g.FanPercent)}%  power={N(g.PowerW)}W\n" +
                          $"         driver={g.DriverVersionFriendly ?? g.DriverVersion}  loc={g.Location}  " +
                          $"verdict={g.Verdict.Tier}/{g.Verdict.Label}");

    Console.WriteLine($"RAM      {s.Memory.TotalGb} GB {s.Memory.Type} {s.Memory.SpeedMhz}MHz   " +
                      $"{s.Memory.SlotsUsed}/{s.Memory.SlotsTotal} slots   used={N(s.Memory.UsedGb)}GB ({N(s.Memory.LoadPercent)}%)");
    foreach (MemoryStick m in s.Memory.Sticks)
        Console.WriteLine($"           [{m.Slot}] {m.CapacityGb}GB {m.Type} {m.SpeedMhz}MHz {m.Manufacturer} {m.PartNumber}");

    foreach (BBSpecs.Models.DriveInfo d in s.Drives)
        Console.WriteLine($"DRIVE {d.Index}  {d.Model}\n" +
                          $"         {d.Kind} ({d.BusType})  {d.SizeGb}GB  temp={N(d.TempC)}  " +
                          $"life={N(d.HealthPercent)}%  written={N(d.DataWrittenTb)}TB  system={d.IsSystemDrive}\n" +
                          $"         volumes: {(d.Volumes.Count == 0 ? "(none)" : string.Join(", ", d.Volumes.Select(v => $"{v.Letter} {v.FreeGb}/{v.TotalGb}GB free")))}");

    Console.WriteLine($"BOARD    {s.Board.Manufacturer} {s.Board.Product}   " +
                      $"BIOS {s.Board.BiosVersion} ({s.Board.BiosDate})   temp={N(s.Board.TempC)}");

    Console.WriteLine($"NET      {s.Network.ConnectionType}  online={s.Network.Online}  ping={N(s.Network.PingMs)}ms  " +
                      $"down={N(s.Network.DownloadKbps, "0.0")}KB/s  up={N(s.Network.UploadKbps, "0.0")}KB/s");
    Console.WriteLine($"         primary={s.Network.Primary?.Description ?? "none"} " +
                      $"({s.Network.Primary?.Kind}) ip={s.Network.Primary?.Ipv4} " +
                      $"link={N(s.Network.Primary?.LinkSpeedMbps)}Mbps");
    Console.WriteLine($"         wifi={(s.Network.Wifi is null ? "none" : $"{s.Network.Wifi.Ssid} {s.Network.Wifi.SignalPercent}% {s.Network.Wifi.RadioType} {s.Network.Wifi.Band} ch{s.Network.Wifi.Channel} {s.Network.Wifi.Security}")}");
    Console.WriteLine($"         vpn={s.Network.Vpn.Active} {s.Network.Vpn.Name}   adapters listed={s.Network.Adapters.Count}");

    Console.WriteLine($"FANS     {s.Fans.Count} reporting");
    foreach (FanInfo f in s.Fans)
        Console.WriteLine($"           {f.Source} / {f.Name}: {f.Rpm} RPM {N(f.Percent)}%");

    Console.WriteLine();
    Console.WriteLine($"OVERALL  {s.System.Score}/100  {s.System.Overall.Label}");
    foreach (string h in s.System.Highlights) Console.WriteLine($"   + {h}");
    foreach (string w in s.System.Watchouts) Console.WriteLine($"   - {w}");

    File.WriteAllText(outPath, Json.Serialize(s));
    Console.WriteLine($"\nWrote {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KB)");
}

static string N(double? value, string format = "0.#") => value?.ToString(format) ?? "—";

/// <summary>
/// The sensor library's own diagnostic dump. When a machine reports no
/// temperatures at all, the Ring0 section of this says exactly why the kernel
/// driver did or didn't load: which beats inferring it from the outside.
/// </summary>
static void DumpDriverReport()
{
#if BBSPECS_WINDOWS
    Console.WriteLine($"Elevated: {Elevation.IsElevated} ({Elevation.RoleName})");
    Console.WriteLine();

    var hub = new BBSpecs.Platform.Windows.SensorHub();
    hub.Start();
    hub.Refresh();
    Thread.Sleep(600);
    hub.Refresh();

    Console.WriteLine($"Sensors ready: {hub.Ready}");
    Console.WriteLine($"Note: {hub.Note}");
    Console.WriteLine();
    Console.WriteLine(hub.Report());

    hub.Dispose();
#else
    Console.WriteLine("The driver report is a Windows-only diagnostic. On Linux, run");
    Console.WriteLine("  dotnet run --project tools/SnapshotDump -- --sensors");
    Console.WriteLine("to list the hwmon chips the kernel is exposing.");
#endif
}

/// <summary>
/// Every sensor the platform offers, exactly as it is named. Blank readings in
/// the app almost always mean the name isn't here at all.
/// </summary>
static void DumpSensors()
{
    Console.WriteLine($"Elevated: {Elevation.IsElevated} ({Elevation.RoleName})");
    Console.WriteLine();

#if BBSPECS_WINDOWS
    var hub = new BBSpecs.Platform.Windows.SensorHub();
    hub.Start();
    hub.Refresh();
    Thread.Sleep(900);
    hub.Refresh();

    Console.WriteLine($"Sensor layer ready: {hub.Ready}   {hub.Note}");
    Console.WriteLine();

    foreach (LibreHardwareMonitor.Hardware.IHardware hw in hub.Hardware)
    {
        Console.WriteLine($"[{hw.HardwareType}] {hw.Name}   ({hw.Identifier})");

        foreach (LibreHardwareMonitor.Hardware.ISensor s in
                 BBSpecs.Platform.Windows.SensorHub.AllSensors(hw).OrderBy(s => s.SensorType.ToString()))
            Console.WriteLine($"    {s.SensorType,-12} {s.Name,-34} = {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "null")}");

        Console.WriteLine();
    }

    if (hub.Hardware.Count == 0)
        Console.WriteLine("No hardware was enumerated at all: the sensor driver did not load.");

    hub.Dispose();
#else
    foreach (BBSpecs.Platform.Linux.HwmonChip chip in BBSpecs.Platform.Linux.Hwmon.Chips())
    {
        Console.WriteLine($"[hwmon] {chip.Name}   ({chip.Path})");

        foreach ((string label, double celsius) in BBSpecs.Platform.Linux.Hwmon.Temperatures(chip))
            Console.WriteLine($"    Temperature  {label,-34} = {celsius:0.#} C");

        foreach ((string label, double rpm, double? percent) in BBSpecs.Platform.Linux.Hwmon.Fans(chip))
            Console.WriteLine($"    Fan          {label,-34} = {rpm:0} RPM  {(percent.HasValue ? percent.Value + "%" : "")}");

        double? watts = BBSpecs.Platform.Linux.Hwmon.PowerWatts(chip);
        if (watts is not null) Console.WriteLine($"    Power        {"",-34} = {watts:0.#} W");

        Console.WriteLine();
    }
#endif
}

/// <summary>
/// Says which optional helpers are present, so a missing reading on Linux is
/// traceable to a missing package rather than looking like a bug.
/// </summary>
static void ReportOptionalTools()
{
    (string tool, string gives)[] tools =
    [
        ("lspci", "graphics card model"),
        ("nvidia-smi", "NVIDIA temperature, VRAM and driver"),
        ("dmidecode", "memory slot layout (needs root)"),
        ("smartctl", "drive health and lifetime writes (needs root)"),
        ("nmcli", "Wi-Fi network name and security"),
        ("iw", "Wi-Fi signal, rates and standard"),
        ("lsblk", "drive serial numbers"),
    ];

    Console.WriteLine("Optional tools:");
    foreach ((string tool, string gives) in tools)
        Console.WriteLine($"  [{(Shell.Exists(tool) ? "x" : " ")}] {tool,-12} {gives}");
    Console.WriteLine();
}
