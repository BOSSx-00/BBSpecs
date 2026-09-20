using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using BBSpecs.Models;
using BBSpecs.Services;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Reads installed drivers out of WMI.
///
/// Win32_PnPSignedDriver is the only place Windows keeps a driver's release
/// date, and enumerating it is slow: a few seconds on a machine with a lot of
/// devices. That is why it runs on its own thread and only every half hour.
///
/// The class filter is doing real work. A full sweep returns hundreds of
/// entries, most of them Microsoft's own bus and HID drivers that nobody ever
/// updates and nobody should. Only the four categories worth showing to
/// someone are kept.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDrivers
{
    public static List<DriverInfo> Read(BoardInfo board)
    {
        var found = new List<DriverInfo>();

        const string wql = """
            SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, Manufacturer
            FROM Win32_PnPSignedDriver
            WHERE DeviceClass = 'DISPLAY' OR DeviceClass = 'MEDIA'
               OR DeviceClass = 'NET' OR DeviceClass = 'HDC'
            """;

        foreach (ManagementObject driver in Wmi.Query(wql))
        {
            string device = driver.Str("DeviceName");
            if (device.Length == 0) continue;

            if (Drivers.Category(driver.Str("DeviceClass")) is not string category) continue;

            // Virtual adapters, loopbacks and the software side of VPN clients
            // all land in NET and are noise on a page about hardware.
            if (category == "Network" && IsVirtual(device)) continue;

            // MEDIA is worse: nine of its entries are Microsoft's own media
            // pipeline plumbing, which buried the one real sound card on a test
            // machine. Nobody has ever updated a "Streaming Clock Proxy".
            if (category == "Audio" && IsPlumbing(device)) continue;

            string vendor = driver.Str("Manufacturer");
            DateTime? released = ParseWmiDate(driver.Str("DriverDate"));
            int? age = released is DateTime date ? (int)(DateTime.Now - date).TotalDays : null;

            found.Add(new DriverInfo
            {
                Device = device,
                Category = category,
                Version = Blank(driver.Str("DriverVersion")),
                Date = released?.ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
                AgeDays = age,
                Vendor = Blank(vendor),
                VendorUrl = Drivers.VendorUrl(vendor, device),
                Verdict = Drivers.Rate(category, age, vendor),
            });
        }

        // The BIOS is not a driver, but it is firmware that goes out of date in
        // exactly the same way, and this is the page someone would look for it on.
        if (board.BiosDate is string biosDate && ParseFriendlyDate(biosDate) is DateTime bios)
        {
            int age = (int)(DateTime.Now - bios).TotalDays;

            found.Add(new DriverInfo
            {
                Device = $"{board.Manufacturer} {board.Product}".Trim(),
                Category = "System firmware",
                Version = board.BiosVersion,
                Date = bios.ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
                AgeDays = age,
                Vendor = Blank(board.Manufacturer),
                VendorUrl = null,
                Verdict = Drivers.Rate("System firmware", age, board.Manufacturer),
            });
        }

        // Firmware first, then graphics: the two that matter, before the rest.
        // Within a category, oldest first, because that is what needs looking at.
        return found
            .OrderBy(d => d.Category switch
            {
                "System firmware" => 0,
                "Graphics" => 1,
                "Network" => 2,
                "Audio" => 3,
                _ => 4,
            })
            .ThenByDescending(d => d.AgeDays ?? -1)
            .ToList();
    }

    /// <summary>
    /// Windows' internal media pipeline, which appears in the audio class but
    /// is not a device anyone has or could update.
    /// </summary>
    private static bool IsPlumbing(string device)
    {
        string[] giveaways =
        [
            "streaming clock proxy", "streaming quality manager", "streaming service proxy",
            "streaming tee", "kernel streaming", "audio device graph", "streaming device proxy",
        ];

        return giveaways.Any(g => device.Contains(g, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVirtual(string device)
    {
        string[] giveaways =
        [
            "virtual", "loopback", "tap-", "tunnel", "wan miniport", "teredo",
            "bluetooth device (personal area", "microsoft kernel debug", "wintun",
        ];

        return giveaways.Any(g => device.Contains(g, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>WMI dates arrive as yyyymmddHHMMSS with a timezone suffix.</summary>
    private static DateTime? ParseWmiDate(string value)
    {
        if (value.Length < 8) return null;

        return DateTime.TryParseExact(value[..8], "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>The BIOS date has already been formatted for display by then.</summary>
    private static DateTime? ParseFriendlyDate(string value) =>
        DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;

    private static string? Blank(string value) => value.Length == 0 ? null : value;
}
