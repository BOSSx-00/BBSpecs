using System.Text.RegularExpressions;
using BBSpecs.Models;

namespace BBSpecs.Services;

/// <summary>
/// Turns raw specs into plain-English ratings. Everything here is a heuristic —
/// deliberately so. The goal is to tell a beginner "this is fine" or "this is the
/// part holding you back", not to produce a benchmark score.
/// </summary>
public static partial class Verdicts
{
    // Ratings are anchored to the current year so the app ages gracefully instead
    // of hard-coding "2024 is new".
    private static int ThisYear => DateTime.Now.Year;

    // ---------------------------------------------------------------- CPU ----

    [GeneratedRegex(@"\bi([3579])[- ]?(\d{4,5})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreRx();

    [GeneratedRegex(@"\bUltra\s+([3579])\s+(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltraRx();

    [GeneratedRegex(@"\bRyzen\s+(?:AI\s+)?(?:Threadripper\s+)?([3579])\s+(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenRx();

    [GeneratedRegex(@"\b(Pentium|Celeron|Atom|Athlon|A\d-\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex BudgetCpuRx();

    /// <summary>Best-effort release year and product family for a CPU model string.</summary>
    public static (int? Year, string? Family, int Tier) ReadCpuModel(string name)
    {
        // Tier: 0 = budget, 3 = entry (i3/R3), 5 = mainstream, 7 = high, 9 = flagship
        Match ultra = IntelUltraRx().Match(name);
        if (ultra.Success)
        {
            int tier = int.Parse(ultra.Groups[1].Value);
            int model = int.Parse(ultra.Groups[2].Value);
            // Series 1 (2xx = Meteor Lake, 2023); Series 2 (2xx V/H = 2024+)
            int year = model >= 200 ? 2024 : 2023;
            return (year, $"Intel Core Ultra {tier}", tier);
        }

        Match intel = IntelCoreRx().Match(name);
        if (intel.Success)
        {
            int tier = int.Parse(intel.Groups[1].Value);
            string model = intel.Groups[2].Value;
            int gen = model.Length == 5 ? int.Parse(model[..2]) : int.Parse(model[..1]);
            int year = gen switch
            {
                <= 4 => 2013, 5 => 2015, 6 => 2015, 7 => 2016, 8 => 2017, 9 => 2018,
                10 => 2019, 11 => 2021, 12 => 2021, 13 => 2022, 14 => 2023,
                _ => 2024,
            };
            return (year, $"Intel Core i{tier}, {Ordinal(gen)} generation", tier);
        }

        Match ryzen = RyzenRx().Match(name);
        if (ryzen.Success)
        {
            int tier = int.Parse(ryzen.Groups[1].Value);
            int model = int.Parse(ryzen.Groups[2].Value);
            int series = model / 1000;
            int year = series switch
            {
                1 => 2017, 2 => 2018, 3 => 2019, 4 => 2020, 5 => 2020,
                6 => 2022, 7 => 2022, 8 => 2024, 9 => 2024,
                _ => 2025,
            };
            return (year, $"AMD Ryzen {tier} {series}000 series", tier);
        }

        if (BudgetCpuRx().IsMatch(name)) return (null, "Budget processor", 0);
        if (name.Contains("Xeon", StringComparison.OrdinalIgnoreCase)) return (null, "Intel Xeon (workstation/server)", 7);
        if (name.Contains("EPYC", StringComparison.OrdinalIgnoreCase)) return (null, "AMD EPYC (server)", 9);
        return (null, null, 5);
    }

    public static Verdict RateCpu(string name, int cores, int threads, int? year, int tier)
    {
        int age = year is int y ? ThisYear - y : 6;

        if (tier == 0 || cores <= 2)
            return Verdict.Make("poor", "Upgrade Soon",
                $"{cores} core{(cores == 1 ? "" : "s")} on an entry-level chip. Fine for web and email, but it will feel slow with modern apps.");

        // Age dominates, but gently: processors have improved slowly enough that a
        // strong chip from five or six years ago is still a perfectly good one.
        if (age >= 10)
            return Verdict.Make("poor", "Outdated",
                $"Roughly {age} years old. Modern systems and browsers expect more than this chip can give.");

        if (age >= 8)
            return Verdict.Make("aging", "Upgrade Soon",
                $"About {age} years old. It still works, but you'll notice it struggling with newer games and heavy multitasking.");

        if (age >= 6)
            return cores >= 8
                ? Verdict.Make("ok", "Still Good",
                    $"{cores} cores and around {age} years old — plenty for everyday use, a step behind for heavy work.")
                : Verdict.Make("aging", "Upgrade Soon",
                    $"{cores} cores and around {age} years old. Fine day to day, but starting to show its age.");

        if (tier >= 7 && cores >= 8)
            return Verdict.Make("excellent", "Amazing",
                $"{cores} cores, {threads} threads, and only about {age} year{(age == 1 ? "" : "s")} old. This handles gaming, editing and heavy multitasking without complaint.");

        if (cores >= 6)
            return Verdict.Make("good", "Good",
                $"{cores} cores and {threads} threads. A solid, modern processor for everyday use and gaming.");

        return Verdict.Make("ok", "Decent",
            $"{cores} cores. Comfortable for browsing, office work and light gaming.");
    }

    /// <summary>How the CPU is doing right now, thermally.</summary>
    public static Verdict RateCpuTemp(double? temp)
    {
        if (temp is not double t) return Verdict.Unknown("No temperature sensor was found for this processor.");
        return t switch
        {
            < 50 => Verdict.Make("excellent", "Cool", $"{t:0}°C — running cool and comfortable."),
            < 70 => Verdict.Make("good", "Normal", $"{t:0}°C — a perfectly normal working temperature."),
            < 85 => Verdict.Make("ok", "Warm", $"{t:0}°C — warm, which is expected under load. Nothing to worry about."),
            < 95 => Verdict.Make("aging", "Hot", $"{t:0}°C — hot. Worth checking your fans and dusting out the case."),
            _ => Verdict.Make("poor", "Too Hot", $"{t:0}°C — this is high enough that your PC may be slowing itself down to cope. Check cooling."),
        };
    }

    public static string CoreHealth(double? temp, double? load)
    {
        if (temp is double t)
            return t < 70 ? "good" : t < 88 ? "warm" : "hot";
        if (load is double l)
            return l < 85 ? "good" : "warm";
        return "unknown";
    }

    // ---------------------------------------------------------------- GPU ----

    [GeneratedRegex(@"\b(?:GeForce\s+)?(RTX|GTX)\s*(\d{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaRx();

    [GeneratedRegex(@"\bRadeon\s+(?:RX\s+)?(\d{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRx();

    [GeneratedRegex(@"\bArc\s+([AB])(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex ArcRx();

    public static (int? Year, bool Integrated) ReadGpuModel(string name)
    {
        if (IsIntegrated(name)) return (null, true);

        Match nv = NvidiaRx().Match(name);
        if (nv.Success)
        {
            string prefix = nv.Groups[1].Value.ToUpperInvariant();
            int model = int.Parse(nv.Groups[2].Value);
            int series = model >= 1000 ? model / 1000 : model / 100;
            int? year = prefix == "RTX"
                ? series switch { 2 => 2018, 3 => 2020, 4 => 2022, 5 => 2025, _ => null }
                : series switch { 9 => 2014, 10 => 2016, 16 => 2019, _ => (model >= 1600 ? 2019 : model >= 1000 ? 2016 : null) };
            return (year, false);
        }

        Match rx = RadeonRx().Match(name);
        if (rx.Success)
        {
            int model = int.Parse(rx.Groups[1].Value);
            int series = model >= 1000 ? model / 1000 : model / 100;
            int? year = series switch
            {
                4 => 2017, 5 => 2018, 6 => 2020, 7 => 2022, 9 => 2025, _ => null,
            };
            return (year, false);
        }

        Match arc = ArcRx().Match(name);
        if (arc.Success)
            return (arc.Groups[1].Value.Equals("B", StringComparison.OrdinalIgnoreCase) ? 2024 : 2022, false);

        return (null, false);
    }

    public static bool IsIntegrated(string name) =>
        name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Vega", StringComparison.OrdinalIgnoreCase) && name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase);

    public static Verdict RateGpu(string name, double? vramGb, int? year, bool integrated)
    {
        if (name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
            return Verdict.Make("poor", "Driver Missing",
                "Windows is using a generic display driver. Installing the proper graphics driver will make everything smoother.");

        if (integrated)
            return Verdict.Make("ok", "Built-In Graphics",
                "This graphics is built into your processor. Great for video, browsing and office work — not made for serious gaming.");

        int age = year is int y ? ThisYear - y : 5;
        double vram = vramGb ?? 0;

        // Video memory matters more than age here: a card with 8 GB still runs
        // today's games at sensible settings even if it shipped years ago.
        if (age >= 11 || (vram > 0 && vram < 3))
            return Verdict.Make("poor", "Outdated",
                vram > 0
                    ? $"{vram:0.#} GB of video memory and roughly {age} years old. Modern games and apps need more."
                    : $"Roughly {age} years old — modern games and apps need more.");

        if (age >= 8 || (vram > 0 && vram < 6))
            return Verdict.Make("aging", "Upgrade Soon",
                $"{(vram > 0 ? $"{vram:0.#} GB of video memory, " : "")}about {age} years old. Fine for older or lighter games at modest settings.");

        if (vram >= 12 && age <= 4)
            return Verdict.Make("excellent", "Amazing",
                $"{vram:0.#} GB of video memory on a card only about {age} year{(age == 1 ? "" : "s")} old. Comfortable at high settings, including 1440p and 4K.");

        if (vram >= 8)
            return Verdict.Make("good", "Good",
                $"{vram:0.#} GB of video memory. A strong card for 1080p and 1440p gaming.");

        return Verdict.Make("ok", "Decent",
            $"{(vram > 0 ? $"{vram:0.#} GB of video memory. " : "")}Handles most games at 1080p if you keep settings sensible.");
    }

    public static Verdict RateGpuTemp(double? temp)
    {
        if (temp is not double t) return Verdict.Unknown("No temperature sensor was found for this graphics card.");
        return t switch
        {
            < 50 => Verdict.Make("excellent", "Cool", $"{t:0}°C — idle or barely working."),
            < 75 => Verdict.Make("good", "Normal", $"{t:0}°C — exactly where a graphics card should be."),
            < 85 => Verdict.Make("ok", "Warm", $"{t:0}°C — warm but completely normal while gaming."),
            < 92 => Verdict.Make("aging", "Hot", $"{t:0}°C — hot. Make sure the case has airflow and the fans are clean."),
            _ => Verdict.Make("poor", "Too Hot", $"{t:0}°C — high enough that the card is likely slowing itself down. Check cooling."),
        };
    }

    // ---------------------------------------------------------------- RAM ----

    public static Verdict RateMemory(double totalGb, int? speedMhz, string? type)
    {
        string speedNote = speedMhz is int s and > 0 ? $" at {s} MHz" : "";
        string typeNote = string.IsNullOrEmpty(type) ? "" : $" of {type}";

        return totalGb switch
        {
            < 6 => Verdict.Make("poor", "Upgrade Soon",
                $"{totalGb:0.#} GB is below what Windows comfortably needs. Adding memory is the cheapest speed-up you can buy."),
            < 10 => Verdict.Make("aging", "Just Enough",
                $"{totalGb:0.#} GB{typeNote}{speedNote}. Fine for browsing and office work, tight for gaming or lots of tabs."),
            < 20 => Verdict.Make("good", "Good",
                $"{totalGb:0.#} GB{typeNote}{speedNote} — the sweet spot for gaming and everyday use."),
            < 40 => Verdict.Make("excellent", "Amazing",
                $"{totalGb:0.#} GB{typeNote}{speedNote}. Plenty of headroom for gaming, editing and heavy multitasking."),
            _ => Verdict.Make("excellent", "Amazing",
                $"{totalGb:0.#} GB{typeNote}{speedNote}. Far more than most people will ever use."),
        };
    }

    // -------------------------------------------------------------- Drives ----

    public static Verdict RateDrive(string kind, double? healthPercent, bool isSystem)
    {
        if (healthPercent is double h && h < 20)
            return Verdict.Make("poor", "Replace Soon",
                $"This drive reports {h:0}% of its life remaining. Back up anything important and plan a replacement.");

        if (healthPercent is double h2 && h2 < 50)
            return Verdict.Make("aging", "Watch This One",
                $"{h2:0}% of its rated life remaining. Still healthy, but keep an eye on it and keep backups.");

        if (kind.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return Verdict.Make("excellent", "Amazing",
                "An NVMe solid-state drive — the fastest kind of storage you can put in a PC.");

        if (kind.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            return Verdict.Make("good", "Good",
                "A solid-state drive. Fast, silent and far quicker than a spinning hard drive.");

        if (kind.Contains("Hard Drive", StringComparison.OrdinalIgnoreCase))
            return isSystem
                ? Verdict.Make("poor", "Upgrade Soon",
                    "Windows is installed on a spinning hard drive. Moving to an SSD is the single biggest speed improvement you can make.")
                : Verdict.Make("ok", "Fine For Storage",
                    "A spinning hard drive. Slow for programs, but perfectly good for files, photos and backups.");

        return Verdict.Make("ok", "Working", "This drive is connected and working normally.");
    }

    public static Verdict RateSpace(double freeGb, double totalGb)
    {
        if (totalGb <= 0) return Verdict.Unknown("No usable space was reported.");
        double pctFree = freeGb / totalGb * 100.0;

        if (pctFree < 5 || freeGb < 10)
            return Verdict.Make("poor", "Almost Full",
                $"Only {freeGb:0.#} GB free. Windows needs breathing room — free up space to avoid slowdowns and failed updates.");
        if (pctFree < 15)
            return Verdict.Make("aging", "Getting Full",
                $"{freeGb:0.#} GB free ({pctFree:0}%). Worth clearing some space soon.");
        if (pctFree < 35)
            return Verdict.Make("good", "Healthy", $"{freeGb:0.#} GB free ({pctFree:0}%) — comfortable.");
        return Verdict.Make("excellent", "Lots Of Room", $"{freeGb:0.#} GB free ({pctFree:0}%) — plenty of space.");
    }

    public static Verdict RateDriveTemp(double? temp)
    {
        if (temp is not double t) return Verdict.Unknown("This drive doesn't report a temperature.");
        return t switch
        {
            < 45 => Verdict.Make("excellent", "Cool", $"{t:0}°C — nice and cool."),
            < 60 => Verdict.Make("good", "Normal", $"{t:0}°C — a normal working temperature."),
            < 70 => Verdict.Make("ok", "Warm", $"{t:0}°C — warm, but within spec."),
            _ => Verdict.Make("poor", "Too Hot", $"{t:0}°C — hot for a drive. Check airflow around it."),
        };
    }

    // --------------------------------------------------------------- Wi-Fi ----

    public static string SignalLabel(int percent) => percent switch
    {
        >= 80 => "Excellent",
        >= 60 => "Good",
        >= 40 => "Fair",
        >= 20 => "Weak",
        _ => "Very Weak",
    };

    public static Verdict RateWifi(int signalPercent, string? radioType, string? band)
    {
        string where = band is null ? "" : $" on {band}";
        string kind = radioType is null ? "" : $" using {radioType}";

        if (signalPercent >= 75)
            return Verdict.Make("excellent", "Excellent Signal",
                $"{signalPercent}% signal{where}{kind}. You're well within range.");
        if (signalPercent >= 55)
            return Verdict.Make("good", "Good Signal",
                $"{signalPercent}% signal{where}{kind}. Solid for streaming and gaming.");
        if (signalPercent >= 35)
            return Verdict.Make("ok", "Fair Signal",
                $"{signalPercent}% signal{where}. You may see slowdowns — moving closer to the router would help.");
        return Verdict.Make("poor", "Weak Signal",
            $"{signalPercent}% signal{where}. Expect drop-outs. Move closer to the router or use a cable.");
    }

    public static Verdict RateConnection(string type, bool online, double? linkMbps, int? pingMs)
    {
        if (!online)
            return Verdict.Make("poor", "Offline", "BBSpecs couldn't reach the internet from this machine.");

        string ping = pingMs is int p ? $" Response time is {p} ms." : "";

        if (type == "Ethernet")
        {
            string speed = linkMbps is double m and > 0 ? $" at {FormatLinkSpeed(m)}" : "";
            return Verdict.Make("excellent", "Wired Connection",
                $"You're plugged in with a network cable{speed}. This is the most stable connection you can have.{ping}");
        }

        if (type == "Wi-Fi")
            return Verdict.Make("good", "Wireless Connection",
                $"You're connected over Wi-Fi.{ping}");

        return Verdict.Make("ok", "Connected", $"You're online.{ping}");
    }

    public static string FormatLinkSpeed(double mbps) =>
        mbps >= 1000 ? $"{mbps / 1000:0.#} Gbps" : $"{mbps:0} Mbps";

    // ------------------------------------------------------------- Overall ----

    private static int TierScore(string tier) => tier switch
    {
        "excellent" => 100,
        "good" => 78,
        "ok" => 58,
        "aging" => 36,
        "poor" => 14,
        _ => 55,
    };

    /// <summary>
    /// Blends the individual part ratings into one headline verdict, and collects
    /// the "this is great" / "this is what to fix first" bullet points.
    /// </summary>
    public static (Verdict Overall, int Score, List<string> Highlights, List<string> Watchouts) RateSystem(
        Verdict cpu, Verdict gpu, Verdict ram, Verdict systemDrive,
        string cpuName, string gpuName, double ramGb, string driveKind)
    {
        // Weighted toward the parts people actually feel day to day.
        double score =
            TierScore(cpu.Tier) * 0.30 +
            TierScore(gpu.Tier) * 0.25 +
            TierScore(ram.Tier) * 0.25 +
            TierScore(systemDrive.Tier) * 0.20;

        int rounded = (int)Math.Round(score);

        List<string> highlights = [];
        List<string> watchouts = [];

        void Sort(Verdict v, string part, string what)
        {
            if (v.Tier is "excellent" or "good") highlights.Add($"{part}: {what}");
            else if (v.Tier is "aging" or "poor") watchouts.Add($"{part}: {v.Reason}");
        }

        Sort(cpu, "Processor", cpuName);
        Sort(gpu, "Graphics", gpuName);
        Sort(ram, "Memory", $"{ramGb:0.#} GB installed");
        Sort(systemDrive, "Main drive", driveKind);

        Verdict overall = rounded switch
        {
            >= 88 => Verdict.Make("excellent", "Amazing PC",
                "Every major part of this machine is modern and well matched. You can run just about anything."),
            >= 70 => Verdict.Make("good", "Good PC",
                "A well-rounded machine. It handles everyday work and gaming comfortably."),
            >= 52 => Verdict.Make("ok", "Decent PC",
                "Perfectly usable day to day. One or two parts are starting to hold it back."),
            >= 32 => Verdict.Make("aging", "Upgrade Soon",
                "This machine still works, but it's behind what modern software expects. See the notes below for what to fix first."),
            _ => Verdict.Make("poor", "Outdated",
                "Most parts of this machine are well past their prime. An upgrade would make a dramatic difference."),
        };

        return (overall, rounded, highlights, watchouts);
    }

    private static string Ordinal(int n)
    {
        if (n is >= 11 and <= 13) return n + "th";
        return (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
    }
}
