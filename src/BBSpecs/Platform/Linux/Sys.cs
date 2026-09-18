namespace BBSpecs.Platform.Linux;

/// <summary>
/// Reads the kernel's virtual filesystems. Every accessor answers null or an
/// empty sequence when a file is missing, unreadable or in an unexpected shape —
/// sysfs layouts vary between kernels, drivers and distributions, and a machine
/// that lays things out differently should lose one row, not the whole app.
/// </summary>
public static class Sys
{
    public static string? Text(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    public static string[] Lines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch { return []; }
    }

    public static double? Number(string path)
    {
        string? text = Text(path);
        return double.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    public static long? Integer(string path)
    {
        string? text = Text(path);
        return long.TryParse(text, out long value) ? value : null;
    }

    public static string[] Directories(string path, string pattern = "*")
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : [];
        }
        catch { return []; }
    }

    public static string[] Files(string path, string pattern = "*")
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : [];
        }
        catch { return []; }
    }

    /// <summary>Resolves a sysfs symlink (driver, device, …) to its real path.</summary>
    public static string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                                     ?? File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName;
        }
        catch { return null; }
    }

    /// <summary>The last path segment of a symlink target — e.g. the driver name.</summary>
    public static string? LinkName(string path)
    {
        string? target = LinkTarget(path);
        return target is null ? null : Path.GetFileName(target.TrimEnd('/'));
    }

    /// <summary>Parses "key: value" and "key value" style files such as /proc/meminfo.</summary>
    public static Dictionary<string, string> KeyValues(string path, char separator = ':')
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in Lines(path))
        {
            int at = line.IndexOf(separator);
            if (at <= 0) continue;

            string key = line[..at].Trim();
            string value = line[(at + 1)..].Trim();
            if (key.Length > 0) map[key] = value;
        }

        return map;
    }
}

/// <summary>One hwmon chip — the kernel's uniform interface to temperature, fan and power sensors.</summary>
public sealed record HwmonChip(string Path, string Name);

public static class Hwmon
{
    private const string Root = "/sys/class/hwmon";

    public static List<HwmonChip> Chips()
    {
        var chips = new List<HwmonChip>();

        foreach (string dir in Sys.Directories(Root, "hwmon*"))
        {
            string? name = Sys.Text(Path.Combine(dir, "name"));
            if (!string.IsNullOrEmpty(name)) chips.Add(new HwmonChip(dir, name));
        }

        return chips;
    }

    public static IEnumerable<HwmonChip> Named(IEnumerable<HwmonChip> chips, params string[] names) =>
        chips.Where(c => names.Any(n => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Label and °C for every temperature input on a chip.</summary>
    public static List<(string Label, double Celsius)> Temperatures(HwmonChip chip)
    {
        var readings = new List<(string, double)>();

        foreach (string file in Sys.Files(chip.Path, "temp*_input").OrderBy(f => f, StringComparer.Ordinal))
        {
            double? milli = Sys.Number(file);
            if (milli is null) continue;

            string prefix = file[..^"_input".Length];
            string label = Sys.Text(prefix + "_label") ?? Path.GetFileName(prefix);
            readings.Add((label, milli.Value / 1000.0));
        }

        return readings;
    }

    /// <summary>Label, RPM and (where the driver exposes pwm) a 0-100 output figure.</summary>
    public static List<(string Label, double Rpm, double? Percent)> Fans(HwmonChip chip)
    {
        var readings = new List<(string, double, double?)>();

        foreach (string file in Sys.Files(chip.Path, "fan*_input").OrderBy(f => f, StringComparer.Ordinal))
        {
            double? rpm = Sys.Number(file);
            if (rpm is null or <= 0) continue;

            string prefix = file[..^"_input".Length];
            string label = Sys.Text(prefix + "_label") ?? Path.GetFileName(prefix);

            // fan2_input pairs with pwm2, which is 0-255 rather than a percentage.
            string index = new string(Path.GetFileName(prefix).Where(char.IsDigit).ToArray());
            double? raw = Sys.Number(Path.Combine(chip.Path, "pwm" + index));
            double? percent = raw is null ? null : Math.Round(raw.Value / 255.0 * 100.0);

            readings.Add((label, rpm.Value, percent));
        }

        return readings;
    }

    /// <summary>First temperature whose label contains any of the given fragments.</summary>
    public static double? TemperatureLike(HwmonChip chip, params string[] fragments)
    {
        List<(string Label, double Celsius)> readings = Temperatures(chip);

        foreach (string fragment in fragments)
        {
            foreach ((string label, double celsius) in readings)
            {
                if (label.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return celsius;
            }
        }

        return null;
    }

    /// <summary>Average power in watts, if the chip publishes one (microwatts in sysfs).</summary>
    public static double? PowerWatts(HwmonChip chip)
    {
        double? micro = Sys.Number(Path.Combine(chip.Path, "power1_average"))
                        ?? Sys.Number(Path.Combine(chip.Path, "power1_input"));
        return micro is null or <= 0 ? null : micro.Value / 1_000_000.0;
    }
}
