using System.Text.RegularExpressions;

namespace BBSpecs.Services;

/// <summary>Tidies the model strings firmware reports into something readable.</summary>
public static partial class CpuNames
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRx();

    [GeneratedRegex(@"\s*@.*$")]
    private static partial Regex TrailingClockRx();

    [GeneratedRegex(@"^\d+th Gen\s+", RegexOptions.IgnoreCase)]
    private static partial Regex GenPrefixRx();

    [GeneratedRegex(@"\b(Intel|AMD|Genuine|Authentic)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex VendorWordRx();

    [GeneratedRegex(@"\s*\d+-Core Processor\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CoreCountSuffixRx();

    /// <summary>Strips the trademark noise firmware loves: "(R)", "(TM)", "CPU".</summary>
    public static string Clean(string raw) =>
        WhitespaceRx().Replace(raw, " ")
            .Replace("(R)", "", StringComparison.Ordinal)
            .Replace("(TM)", "", StringComparison.Ordinal)
            .Replace("(tm)", "", StringComparison.Ordinal)
            .Replace("(r)", "", StringComparison.Ordinal)
            .Replace("CPU ", "", StringComparison.Ordinal)
            .Trim();

    /// <summary>"11th Gen Intel Core i9-11900KF @ 3.50GHz" becomes "Core i9-11900KF".</summary>
    public static string Shorten(string name)
    {
        string s = TrailingClockRx().Replace(name, "");
        s = GenPrefixRx().Replace(s, "");
        s = VendorWordRx().Replace(s, "");
        s = CoreCountSuffixRx().Replace(s, "");
        return WhitespaceRx().Replace(s, " ").Trim();
    }
}

public static class GpuNames
{
    public static string Vendor(string reported, string name)
    {
        string h = (reported + " " + name).ToLowerInvariant();
        if (h.Contains("nvidia")) return "NVIDIA";
        if (h.Contains("advanced micro") || h.Contains("amd") || h.Contains("ati ") || h.Contains("radeon")) return "AMD";
        if (h.Contains("intel")) return "Intel";
        return string.IsNullOrWhiteSpace(reported) ? "Unknown" : reported;
    }

    /// <summary>
    /// NVIDIA ships driver "610.88" but Windows records it as "32.0.16.1088".
    /// Beginners look for the former, so derive it from the last two parts.
    /// </summary>
    public static string? FriendlyDriverVersion(string vendor, string driver)
    {
        if (vendor != "NVIDIA" || string.IsNullOrEmpty(driver)) return null;

        string[] parts = driver.Split('.');
        if (parts.Length < 4) return null;

        string digits = parts[2] + parts[3];
        if (digits.Length < 5) return null;

        string tail = digits[^5..];
        return $"{tail[..3]}.{tail[3..]}";
    }
}

public static class MemoryNames
{
    /// <summary>SMBIOS memory type codes (also what dmidecode reports numerically).</summary>
    public static string? Type(int smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM",
        24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2",
        29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
        _ => null,
    };

    public static string? FormFactor(int code) => code switch
    {
        8 => "DIMM (desktop)",
        12 => "SODIMM (laptop)",
        13 => "SRIMM",
        9 => "TSOP",
        _ => null,
    };
}
