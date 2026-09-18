using BBSpecs.Models;
using DriveInfo = BBSpecs.Models.DriveInfo;

namespace BBSpecs.Services;

/// <summary>
/// Works out what to spend money on first.
///
/// The rule throughout is cheapest thing that fixes the actual bottleneck — the
/// parts are named so a beginner can search for them, and nothing suggests
/// replacing something that is still doing its job. No prices: they go stale
/// faster than the app ships, and a wrong price is worse than none.
/// </summary>
public static class Upgrades
{
    public static List<Upgrade> For(Snapshot s)
    {
        var picks = new List<Upgrade>();

        AddMemory(picks, s.Memory);
        AddStorage(picks, s.Drives);
        AddGraphics(picks, s.Gpus.FirstOrDefault());
        AddProcessor(picks, s.Cpu);
        AddCooling(picks, s.Cpu);
        AddNetwork(picks, s.Network);

        // Most urgent first, and never overwhelm someone with a shopping list.
        return picks
            .OrderBy(p => p.Priority switch { "high" => 0, "medium" => 1, _ => 2 })
            .Take(6)
            .ToList();
    }

    // ---- memory ----------------------------------------------------------------

    private static void AddMemory(List<Upgrade> picks, MemoryInfo memory)
    {
        if (memory.TotalGb <= 0) return;

        string kind = memory.Type ?? "DDR4";
        bool laptop = memory.Sticks.Any(x =>
            x.FormFactor?.Contains("SODIMM", StringComparison.OrdinalIgnoreCase) == true);
        string form = laptop ? "SODIMM" : "DIMM";

        if (memory.TotalGb < 10)
        {
            picks.Add(new Upgrade
            {
                Part = "Memory",
                Priority = "high",
                Current = $"{memory.TotalGb:0.#} GB installed",
                Pick = kind.StartsWith("DDR5", StringComparison.OrdinalIgnoreCase)
                    ? $"Crucial Pro 16 GB (2 × 8 GB) DDR5-5600 {form} kit"
                    : $"Crucial Pro 16 GB (2 × 8 GB) DDR4-3200 {form} kit",
                Why = "This is the cheapest change that will make the whole machine feel faster. " +
                      "Below about 10 GB, Windows spends its time shuffling memory to disk instead of working.",
            });
            return;
        }

        if (memory.TotalGb < 20)
        {
            picks.Add(new Upgrade
            {
                Part = "Memory",
                Priority = "low",
                Current = $"{memory.TotalGb:0.#} GB installed",
                Pick = kind.StartsWith("DDR5", StringComparison.OrdinalIgnoreCase)
                    ? $"A second matching {kind} {form} kit to reach 32 GB"
                    : $"A second matching {kind} {form} kit to reach 32 GB",
                Why = "16 GB is the sweet spot for gaming and everyday use, so this one is optional. " +
                      "Go to 32 GB only if you edit video, run virtual machines, or keep a lot of tabs open.",
            });
        }

        // Mismatched sticks are the quiet performance tax nobody notices: the whole
        // set drops to the slowest module's speed, and often to single channel.
        List<double> sizes = memory.Sticks.Select(x => x.CapacityGb).Distinct().ToList();
        List<int> rated = memory.Sticks
            .Select(x => x.SpeedMhz ?? 0).Where(v => v > 0).Distinct().ToList();

        bool mixedSizes = sizes.Count > 1;
        bool runningSlow = memory.SpeedMhz is int actual && rated.Count > 0 && rated.Max() > actual + 100;

        if ((mixedSizes || rated.Count > 1) && memory.SlotsUsed > 1)
        {
            string detail = mixedSizes
                ? $"Your sticks are different sizes ({string.Join(" + ", memory.Sticks.Select(x => $"{x.CapacityGb:0.#} GB"))})"
                : "Your sticks are rated at different speeds";

            picks.Add(new Upgrade
            {
                Part = "Memory",
                Priority = runningSlow ? "medium" : "low",
                Current = memory.SpeedMhz is int now
                    ? $"{memory.TotalGb:0.#} GB running at {now} MHz"
                    : $"{memory.TotalGb:0.#} GB, mixed modules",
                Pick = $"One matched {kind} {form} kit of {(memory.TotalGb >= 28 ? "32 GB (2 × 16 GB)" : "16 GB (2 × 8 GB)")}",
                Why = $"{detail}, so they all run at the slowest one's speed" +
                      (runningSlow
                          ? $" — yours are rated for {rated.Max()} MHz but running at {memory.SpeedMhz} MHz. "
                          : ". ") +
                      "A single matched kit fixes that, and it's the cheapest free performance on this list.",
            });
        }
    }

    // ---- storage ---------------------------------------------------------------

    private static void AddStorage(List<Upgrade> picks, List<DriveInfo> drives)
    {
        DriveInfo? system = drives.FirstOrDefault(d => d.IsSystemDrive) ?? drives.FirstOrDefault();

        if (system is not null && system.Kind.Contains("Hard Drive", StringComparison.OrdinalIgnoreCase))
        {
            picks.Add(new Upgrade
            {
                Part = "Main drive",
                Priority = "high",
                Current = $"{system.Model} — a spinning hard drive",
                Pick = "Crucial BX500 1 TB SATA SSD",
                Why = "Windows is installed on a mechanical drive. Moving it to an SSD is the single " +
                      "biggest speed improvement available to this machine — boot and app loading go " +
                      "from tens of seconds to a few. A SATA SSD fits any computer that has a hard drive.",
            });
        }

        // A drive genuinely near the end of its rated life is worth acting on.
        foreach (DriveInfo worn in drives.Where(d => d.HealthPercent is < 20))
        {
            picks.Add(new Upgrade
            {
                Part = "Failing drive",
                Priority = "high",
                Current = $"{worn.Model} — {worn.HealthPercent:0}% of its rated life left",
                Pick = worn.Kind.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                    ? "Crucial P3 Plus 1 TB NVMe SSD"
                    : "Crucial BX500 1 TB SATA SSD",
                Why = "This drive is near the end of its write life. Back up anything on it now and " +
                      "plan a replacement — drives usually give this warning long before they fail, " +
                      "but not always.",
            });
        }

        // Somewhere to actually put things, when everything is nearly full.
        double totalFree = drives.Where(d => !d.IsRemovable).Sum(d => d.Volumes.Sum(v => v.FreeGb));
        double totalSize = drives.Where(d => !d.IsRemovable).Sum(d => d.Volumes.Sum(v => v.TotalGb));

        if (totalSize > 0 && totalFree / totalSize < 0.12)
        {
            picks.Add(new Upgrade
            {
                Part = "Storage space",
                Priority = "medium",
                Current = $"{totalFree:0} GB free across {drives.Count(d => !d.IsRemovable)} drive(s)",
                Pick = "Crucial P3 Plus 2 TB NVMe SSD",
                Why = "Your drives are nearly full, which slows Windows down and eventually breaks " +
                      "updates. Clearing space costs nothing and is worth trying first; if there's " +
                      "nothing left to delete, add a drive.",
            });
        }
    }

    // ---- graphics --------------------------------------------------------------

    private static void AddGraphics(List<Upgrade> picks, GpuInfo? gpu)
    {
        if (gpu is null) return;

        if (gpu.Name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
        {
            picks.Add(new Upgrade
            {
                Part = "Graphics driver",
                Priority = "high",
                Current = "Windows is using a generic display driver",
                Pick = "No purchase needed — install the driver from NVIDIA, AMD or Intel",
                Why = "Your graphics card is working at a fraction of its ability because the proper " +
                      "driver isn't installed. This is free to fix and will change everything.",
            });
            return;
        }

        if (gpu.IsIntegrated)
        {
            picks.Add(new Upgrade
            {
                Part = "Graphics",
                Priority = "low",
                Current = $"{gpu.Name} — built into the processor",
                Pick = "Intel Arc B580 12 GB",
                Why = "Built-in graphics is fine for video, browsing and office work. Only add a card " +
                      "if you want to play modern games — and if you do, this one gives the most " +
                      "frames and video memory per pound at the budget end. Check your power supply " +
                      "and case have room first.",
            });
            return;
        }

        if (gpu.Verdict.Tier is not ("poor" or "aging")) return;

        double vram = gpu.VramTotalGb ?? 0;

        picks.Add(new Upgrade
        {
            Part = "Graphics card",
            Priority = gpu.Verdict.Tier == "poor" ? "high" : "medium",
            Current = $"{gpu.Name}{(vram > 0 ? $" — {vram:0.#} GB of video memory" : "")}",
            Pick = vram > 0 && vram < 4
                ? "Intel Arc B580 12 GB"
                : "AMD Radeon RX 7600 8 GB, or Intel Arc B580 12 GB for more video memory",
            Why = "This card is the part holding your games back. Both of these are the cheapest " +
                  "cards that still handle modern titles at 1080p, and the Arc's extra video memory " +
                  "buys it a longer life. Check your power supply and case have room before buying.",
        });
    }

    // ---- processor -------------------------------------------------------------

    private static void AddProcessor(List<Upgrade> picks, CpuInfo cpu)
    {
        if (cpu.Verdict.Tier is not ("poor" or "aging")) return;

        picks.Add(new Upgrade
        {
            Part = "Processor",
            Priority = cpu.Verdict.Tier == "poor" ? "medium" : "low",
            Current = $"{cpu.ShortName} — {cpu.PhysicalCores} cores",
            Pick = "AMD Ryzen 5 7600, with a compatible AM5 motherboard and DDR5 memory",
            Why = "A processor swap means a new motherboard and new memory too, so leave it until " +
                  "last — memory, an SSD and a graphics card all cost less and are felt more. When " +
                  "you do, this is the cheapest current chip that won't feel slow for years.",
        });
    }

    private static void AddCooling(List<Upgrade> picks, CpuInfo cpu)
    {
        if (cpu.TempC is not double temp || temp < 90) return;

        picks.Add(new Upgrade
        {
            Part = "Cooling",
            Priority = "medium",
            Current = $"Processor running at {temp:0}°C",
            Pick = "Thermalright Peerless Assassin 120 SE",
            Why = "Your processor is hot enough to be slowing itself down to protect itself, which " +
                  "costs you speed you already paid for. Try cleaning the dust out first — that's " +
                  "free. If it stays hot, this cooler outperforms things costing three times as much.",
        });
    }

    // ---- network ---------------------------------------------------------------

    private static void AddNetwork(List<Upgrade> picks, NetworkInfo network)
    {
        WifiInfo? wifi = network.Wifi;
        if (wifi is null) return;

        bool oldStandard = wifi.RadioType is not null
            && (wifi.RadioType.Contains("802.11n", StringComparison.OrdinalIgnoreCase)
                || wifi.RadioType.Contains("802.11g", StringComparison.OrdinalIgnoreCase)
                || wifi.RadioType.Contains("802.11b", StringComparison.OrdinalIgnoreCase));

        if (oldStandard)
        {
            picks.Add(new Upgrade
            {
                Part = "Wi-Fi",
                Priority = "low",
                Current = $"Connected using {wifi.RadioType}",
                Pick = "Intel Wi-Fi 6E AX210 card",
                Why = "Your wireless is several generations behind, which caps your speed no matter " +
                      "how fast your internet is. A cable is free and always better; if you can't " +
                      "run one, this card is the cheapest real upgrade.",
            });
            return;
        }

        if (wifi.SignalPercent < 40)
        {
            picks.Add(new Upgrade
            {
                Part = "Wi-Fi signal",
                Priority = "medium",
                Current = $"{wifi.SignalPercent}% signal — {wifi.SignalLabel.ToLowerInvariant()}",
                Pick = "No purchase needed — move closer to the router, or run a network cable",
                Why = "Your wireless signal is weak, which causes stuttering and drop-outs that look " +
                      "like a slow internet connection but aren't. Moving the router or the PC is " +
                      "free; a cable fixes it completely. Only buy a mesh extender if neither works.",
            });
        }
    }
}
