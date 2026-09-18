using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Thin, forgiving wrappers over WMI and the registry. Every accessor returns a
/// sensible default rather than throwing — a missing field should blank out one
/// row in the UI, never take down the whole page.
/// </summary>
public static class Wmi
{
    public static List<ManagementObject> Query(string wql, string scope = @"root\CIMV2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            using ManagementObjectCollection results = searcher.Get();
            return results.Cast<ManagementObject>().ToList();
        }
        catch
        {
            return [];
        }
    }

    public static ManagementObject? QueryFirst(string wql, string scope = @"root\CIMV2") =>
        Query(wql, scope).FirstOrDefault();

    public static string Str(this ManagementBaseObject mo, string prop)
    {
        try { return mo[prop]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    public static string? StrOrNull(this ManagementBaseObject mo, string prop)
    {
        string s = mo.Str(prop);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static long Num(this ManagementBaseObject mo, string prop)
    {
        try
        {
            object? v = mo[prop];
            return v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    public static long? NumOrNull(this ManagementBaseObject mo, string prop)
    {
        try
        {
            object? v = mo[prop];
            return v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    public static bool Flag(this ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] is bool b && b; }
        catch { return false; }
    }

    public static DateTime? Date(this ManagementBaseObject mo, string prop)
    {
        try
        {
            string raw = mo.Str(prop);
            return string.IsNullOrEmpty(raw) ? null : ManagementDateTimeConverter.ToDateTime(raw);
        }
        catch { return null; }
    }

    public static string[] StrArray(this ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] as string[] ?? []; }
        catch { return []; }
    }

    /// <summary>Escapes a value going into a WQL string literal (a WHERE clause).</summary>
    public static string EscapeWql(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");

    /// <summary>
    /// Escapes a value going inside the braces of an ASSOCIATORS query. That part
    /// is parsed as an object path rather than a string literal, so backslashes
    /// must stay single — doubling them makes WMI answer "Not found".
    /// </summary>
    public static string EscapePath(string value) => value.Replace("'", @"\'");
}

/// <summary>Registry lookups used for the handful of facts WMI gets wrong or omits.</summary>
public static class Reg
{
    public static string? Read(string key, string value)
    {
        try { return Registry.GetValue(key, value, null)?.ToString(); }
        catch { return null; }
    }

    /// <summary>
    /// Windows reports the friendly release name (24H2, 23H2, ...) only in the
    /// registry, and the true patch level lives in UBR.
    /// </summary>
    public static (string? DisplayVersion, string? Ubr) WindowsRelease()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        return (Read(key, "DisplayVersion") ?? Read(key, "ReleaseId"), Read(key, "UBR"));
    }

    private const string DisplayClass =
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// True VRAM size for a display adapter. Win32_VideoController.AdapterRAM is a
    /// 32-bit field and silently wraps on any card with 4 GB or more, so we read
    /// the 64-bit value the driver publishes instead.
    /// </summary>
    public static long? VideoMemoryBytes(string pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return null;
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null) return null;

            // The PnP id looks like PCI\VEN_10DE&DEV_2484&SUBSYS_...; the driver
            // subkey stores a MatchingDeviceId of the form pci\ven_10de&dev_2484.
            string[] parts = pnpDeviceId.Split('\\');
            if (parts.Length < 2) return null;
            string[] ids = parts[1].Split('&');
            if (ids.Length < 2) return null;
            string needle = $"{parts[0]}\\{ids[0]}&{ids[1]}".ToLowerInvariant();

            foreach (string name in root.GetSubKeyNames())
            {
                if (!name.All(char.IsDigit)) continue;
                using RegistryKey? sub = root.OpenSubKey(name);
                string? match = sub?.GetValue("MatchingDeviceId")?.ToString()?.ToLowerInvariant();
                if (match is null || !match.StartsWith(needle, StringComparison.Ordinal)) continue;

                object? size = sub!.GetValue("HardwareInformation.qwMemorySize");
                if (size is long q && q > 0) return q;
                if (size is byte[] bytes && bytes.Length >= 8) return BitConverter.ToInt64(bytes, 0);

                object? legacy = sub.GetValue("HardwareInformation.MemorySize");
                if (legacy is int i && i > 0) return i;
                if (legacy is byte[] lb && lb.Length >= 4) return BitConverter.ToUInt32(lb, 0);
            }
        }
        catch { /* fall through to the WMI value */ }
        return null;
    }

    /// <summary>
    /// "PCI bus 1, device 0, function 0" — Windows stores this per device, sometimes
    /// as an indirect string with the numbers appended in a (1,0,0) tail.
    /// </summary>
    public static string? DeviceLocation(string pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}");
            string? raw = key?.GetValue("LocationInformation")?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            int tail = raw.LastIndexOf(";(", StringComparison.Ordinal);
            if (tail >= 0)
            {
                string tuple = raw[(tail + 2)..].TrimEnd(')');
                string[] n = tuple.Split(',');
                if (n.Length == 3)
                    return $"PCI bus {n[0].Trim()}, device {n[1].Trim()}, function {n[2].Trim()}";
            }

            // Not an indirect string — use it as-is unless it's an unresolved @path
            return raw.StartsWith('@') ? null : raw;
        }
        catch { return null; }
    }
}
