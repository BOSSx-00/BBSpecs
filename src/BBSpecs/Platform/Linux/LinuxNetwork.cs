using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using BBSpecs.Models;
using BBSpecs.Services;

namespace BBSpecs.Platform.Linux;

/// <summary>
/// The Linux halves of the network picture: the default route straight from the
/// kernel routing table, and wireless details from NetworkManager where it's
/// running, falling back to `iw` on systems that don't use it.
/// </summary>
public sealed partial class LinuxNetwork : NetworkService
{
    [GeneratedRegex(@"signal:\s*(-?\d+)\s*dBm", RegexOptions.IgnoreCase)]
    private static partial Regex SignalRx();

    [GeneratedRegex(@"freq:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FreqRx();

    [GeneratedRegex(@"SSID:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SsidRx();

    [GeneratedRegex(@"Connected to ([0-9a-f:]{17})", RegexOptions.IgnoreCase)]
    private static partial Regex BssidRx();

    [GeneratedRegex(@"(rx|tx) bitrate:\s*([\d.]+)\s*MBit/s(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BitrateRx();

    private string? _defaultRouteInterface;

    protected override void BeginCollect() => _defaultRouteInterface = ReadDefaultRouteInterface();

    protected override bool IsDefaultRoute(NetworkInterface nic) =>
        _defaultRouteInterface is not null
        && string.Equals(nic.Name, _defaultRouteInterface, StringComparison.Ordinal);

    /// <summary>
    /// /proc/net/route lists one line per route with hex fields. The default route
    /// is the one whose destination is all zeroes; where several exist, the kernel
    /// prefers the lowest metric.
    /// </summary>
    private static string? ReadDefaultRouteInterface()
    {
        string? best = null;
        long bestMetric = long.MaxValue;

        foreach (string line in Sys.Lines("/proc/net/route").Skip(1))
        {
            string[] f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 7) continue;
            if (f[1] != "00000000") continue;

            long metric = long.TryParse(f[6], out long m) ? m : long.MaxValue;
            if (metric >= bestMetric) continue;

            bestMetric = metric;
            best = f[0].Trim();
        }

        return best;
    }

    protected override WifiInfo? CollectWifi()
    {
        string? device = FindWirelessInterface();
        if (device is null) return null;

        WifiInfo? wifi = ReadViaNmcli(device) ?? ReadViaIw(device);
        if (wifi is null || string.IsNullOrEmpty(wifi.Ssid)) return null;

        // `iw` knows the negotiated rates and 802.11 generation even when
        // NetworkManager supplied the rest, so let it fill in the gaps.
        EnrichFromIw(wifi, device);

        wifi.SignalLabel = Verdicts.SignalLabel(wifi.SignalPercent);
        wifi.Verdict = Verdicts.RateWifi(wifi.SignalPercent, wifi.RadioType, wifi.Band);
        return wifi;
    }

    /// <summary>A wireless interface is one the kernel gave a "wireless" directory.</summary>
    private static string? FindWirelessInterface()
    {
        foreach (string dir in Sys.Directories("/sys/class/net"))
        {
            string name = Path.GetFileName(dir);
            if (Directory.Exists(Path.Combine(dir, "wireless")) || Directory.Exists(Path.Combine(dir, "phy80211")))
            {
                // Only report a link that's actually up.
                if (Sys.Text(Path.Combine(dir, "operstate")) == "up") return name;
            }
        }

        return null;
    }

    private static WifiInfo? ReadViaNmcli(string device)
    {
        // -t gives colon-separated fields; literal colons inside a value (in a
        // BSSID) arrive backslash-escaped.
        string[] lines = Shell.RunLines("nmcli",
            "-t -f IN-USE,SSID,BSSID,CHAN,FREQ,RATE,SIGNAL,SECURITY device wifi list --rescan no");

        foreach (string line in lines)
        {
            List<string> f = SplitEscaped(line);
            if (f.Count < 8) continue;
            if (f[0] != "*") continue;   // not the connected network

            int signal = int.TryParse(f[6], out int s) ? s : 0;
            double? freq = double.TryParse(
                new string(f[4].TakeWhile(char.IsDigit).ToArray()), out double mhz) ? mhz : null;

            return new WifiInfo
            {
                Ssid = f[1],
                Bssid = string.IsNullOrWhiteSpace(f[2]) ? null : f[2],
                Channel = int.TryParse(f[3], out int ch) ? ch : null,
                SignalPercent = Math.Clamp(signal, 0, 100),
                Band = BandFromFrequency(freq),
                Security = FriendlySecurity(f[7]),
            };
        }

        _ = device;
        return null;
    }

    /// <summary>nmcli -t escapes colons inside values as "\:".</summary>
    private static List<string> SplitEscaped(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { current.Append(line[++i]); }
            else if (line[i] == ':') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(line[i]);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string FriendlySecurity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Open (no password)";

        string v = raw.ToUpperInvariant();
        if (v.Contains("WPA3") || v.Contains("SAE")) return "WPA3 Personal";
        if (v.Contains("802.1X")) return "WPA2 Enterprise";
        if (v.Contains("WPA2")) return "WPA2 Personal";
        if (v.Contains("WPA1") || v.Contains("WPA")) return "WPA Personal";
        if (v.Contains("WEP")) return "WEP (outdated)";
        if (v.Contains("OWE")) return "Enhanced Open (OWE)";
        return raw;
    }

    private static WifiInfo? ReadViaIw(string device)
    {
        string? output = Shell.Run("iw", $"dev {device} link");
        if (output is null || output.Contains("Not connected", StringComparison.OrdinalIgnoreCase)) return null;

        Match ssid = SsidRx().Match(output);
        if (!ssid.Success) return null;

        double? freq = FreqRx().Match(output) is { Success: true } f
            && double.TryParse(f.Groups[1].Value, out double mhz) ? mhz : null;

        int percent = SignalRx().Match(output) is { Success: true } s
            && double.TryParse(s.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dbm)
            ? SignalPercentFromDbm(dbm) : 0;

        return new WifiInfo
        {
            Ssid = ssid.Groups[1].Value.Trim(),
            Bssid = BssidRx().Match(output) is { Success: true } b ? b.Groups[1].Value.ToUpperInvariant() : null,
            SignalPercent = percent,
            Band = BandFromFrequency(freq),
            Channel = ChannelFromFrequency(freq),
        };
    }

    /// <summary>Fills in link rates and the Wi-Fi generation from `iw`.</summary>
    private static void EnrichFromIw(WifiInfo wifi, string device)
    {
        string? output = Shell.Run("iw", $"dev {device} link");
        if (output is null) return;

        foreach (Match m in BitrateRx().Matches(output))
        {
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mbps))
                continue;

            if (m.Groups[1].Value.Equals("rx", StringComparison.OrdinalIgnoreCase)) wifi.ReceiveMbps ??= mbps;
            else wifi.TransmitMbps ??= mbps;

            // The modulation named on the bitrate line tells us the generation.
            wifi.RadioType ??= m.Groups[3].Value switch
            {
                var t when t.Contains("EHT", StringComparison.OrdinalIgnoreCase) => "Wi-Fi 7 (802.11be)",
                var t when t.Contains("HE-", StringComparison.OrdinalIgnoreCase) => "Wi-Fi 6 (802.11ax)",
                var t when t.Contains("VHT", StringComparison.OrdinalIgnoreCase) => "Wi-Fi 5 (802.11ac)",
                var t when t.Contains("MCS", StringComparison.OrdinalIgnoreCase) => "Wi-Fi 4 (802.11n)",
                _ => null,
            };
        }

        if (wifi.SignalPercent == 0 && SignalRx().Match(output) is { Success: true } s
            && double.TryParse(s.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dbm))
            wifi.SignalPercent = SignalPercentFromDbm(dbm);

        wifi.RadioType ??= "Wi-Fi";
    }

    private static int? ChannelFromFrequency(double? megahertz)
    {
        if (megahertz is not double mhz) return null;

        // The standard channel-numbering formulas, per band.
        if (mhz == 2484) return 14;
        if (mhz is >= 2412 and <= 2472) return (int)((mhz - 2407) / 5);
        if (mhz is >= 5160 and <= 5895) return (int)((mhz - 5000) / 5);
        if (mhz is >= 5955 and <= 7115) return (int)((mhz - 5950) / 5);
        return null;
    }
}
