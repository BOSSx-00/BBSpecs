using BBSpecs.Models;

namespace BBSpecs.Services;

/// <summary>
/// Rates drivers by age, and only where age actually means something.
///
/// The honest position: nothing on the machine knows whether a newer driver
/// exists. Windows Update knows about some of them, the vendors know about the
/// rest, and neither offers a way to ask. So BBSpecs reports the one thing it
/// can establish for certain, which is how old the installed one is, and is
/// careful about which categories that is a fair signal for.
///
/// It matters for graphics: those get fixes for specific games every few weeks,
/// and a two-year-old one is genuinely leaving performance behind. It matters
/// much less for a sound card or a network adapter, where a driver Microsoft
/// shipped in 2019 is very often still the newest one there will ever be, and
/// calling it "out of date" would send people hunting for something that does
/// not exist.
/// </summary>
public static class Drivers
{
    public static Verdict Rate(string category, int? ageDays, string? vendor)
    {
        if (ageDays is not int age)
            return Verdict.Unknown("Windows did not report a date for this driver.");

        int years = age / 365;
        string old = years >= 1
            ? $"{years} year{(years == 1 ? "" : "s")} old"
            : $"{age} days old";

        return category switch
        {
            "Graphics" => RateGraphics(age, old),
            "System firmware" => RateFirmware(age, old),
            _ => RateOther(age, old, vendor),
        };
    }

    private static Verdict RateGraphics(int age, string old) => age switch
    {
        < 210 => Verdict.Make("excellent", "Current", $"Installed {old}. Graphics drivers move fast and yours is keeping up."),
        < 400 => Verdict.Make("good", "Recent", $"Installed {old}. Still recent enough that you are not missing much."),
        < 730 => Verdict.Make("ok", "Worth updating",
            $"Installed {old}. Graphics drivers get fixes for individual games every few weeks, so a year " +
            "is long enough to be missing real improvements. This one is free and safe to do."),
        _ => Verdict.Make("aging", "Out of date",
            $"Installed {old}. This is old enough to cause crashes and poor performance in anything recent. " +
            "Updating it is the single easiest fix on this page."),
    };

    private static Verdict RateFirmware(int age, string old) => age switch
    {
        < 730 => Verdict.Make("good", "Recent", $"Released {old}."),
        < 1825 => Verdict.Make("ok", "Worth checking",
            $"Released {old}. Newer BIOS versions usually fix stability and security problems. " +
            "Update it from the motherboard maker's page, never mid-update power cut, and only if " +
            "something is actually wrong: a BIOS update that fails can stop the machine booting."),
        _ => Verdict.Make("aging", "Old",
            $"Released {old}. There are almost certainly several newer versions. Worth reading the " +
            "maker's release notes to see whether any of them fix something you have noticed. " +
            "A failed BIOS update can stop a machine booting, so this one is not worth doing for its own sake."),
    };

    /// <summary>
    /// Audio, network and chipset. A Microsoft driver here being old is normal
    /// and correct, so only a vendor's own driver left for years gets flagged.
    /// </summary>
    private static Verdict RateOther(int age, string old, string? vendor)
    {
        bool microsoft = vendor?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

        if (microsoft)
        {
            return Verdict.Make("good", "Built into Windows",
                $"Dated {old}, which is normal. Windows ships these and they rarely change.");
        }

        return age switch
        {
            < 1095 => Verdict.Make("good", "Fine", $"Installed {old}."),
            < 1825 => Verdict.Make("ok", "Getting on",
                $"Installed {old}. Not urgent. Worth updating if this device has been misbehaving."),
            _ => Verdict.Make("aging", "Old",
                $"Installed {old}. If this device gives you trouble, a newer driver from " +
                $"{vendor ?? "the manufacturer"} is the first thing to try."),
        };
    }

    /// <summary>The page a vendor actually publishes drivers on, when there is one.</summary>
    public static string? VendorUrl(string? vendor, string? device)
    {
        string haystack = $"{vendor} {device}".ToLowerInvariant();

        if (haystack.Contains("nvidia")) return "https://www.nvidia.com/download/index.aspx";
        if (haystack.Contains("advanced micro") || haystack.Contains("amd") || haystack.Contains("radeon"))
            return "https://www.amd.com/en/support";
        if (haystack.Contains("intel")) return "https://www.intel.com/content/www/us/en/download-center/home.html";
        if (haystack.Contains("realtek")) return "https://www.realtek.com/Download";

        return null;
    }

    /// <summary>Groups a Windows device class into something a person would recognise.</summary>
    public static string? Category(string deviceClass) => deviceClass.ToUpperInvariant() switch
    {
        "DISPLAY" => "Graphics",
        "MEDIA" => "Audio",
        "NET" => "Network",
        "HDC" or "SYSTEM" => "Chipset",
        _ => null,
    };
}
