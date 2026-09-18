using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBSpecs.Models;

namespace BBSpecs.Services;

/// <summary>
/// Everything on the Internet tab: which adapter is actually carrying traffic,
/// whether a VPN is in the way, and how fast bytes are moving right now.
///
/// Adapter enumeration, VPN detection and throughput all come from
/// System.Net.NetworkInformation, which behaves the same on both platforms.
/// Only "which adapter holds the default route" and the Wi-Fi link details need
/// operating-system-specific code, and those are the two hooks below.
/// </summary>
public abstract partial class NetworkService
{
    // Adapters whose name or description matches this are tunnels, not real NICs.
    [GeneratedRegex(
        @"wireguard|openvpn|tap-windows|tap-nordvpn|nordlynx|tailscale|zerotier|proton|mullvad|" +
        @"expressvpn|surfshark|cyberghost|private internet access|\bpia\b|windscribe|hotspot shield|" +
        @"anyconnect|fortinet|fortissl|globalprotect|pulse secure|sonicwall|check ?point|" +
        @"sophos|watchguard|softether|hamachi|radmin vpn|wan miniport \((ikev2|l2tp|pptp|sstp)\)|" +
        @"\bvpn\b|^tun\d|^wg\d|^ppp\d", RegexOptions.IgnoreCase)]
    private static partial Regex VpnRx();

    [GeneratedRegex(
        @"virtual|vmware|virtualbox|hyper-v|vethernet|docker|wsl|loopback|bluetooth|" +
        @"teredo|isatap|npcap|bridge|^br-|^veth|^docker|^virbr",
        RegexOptions.IgnoreCase)]
    private static partial Regex VirtualRx();

    // Windows registers dozens of placeholder adapters and Linux exposes plenty of
    // kernel-internal ones. None of them are things a person plugged in.
    [GeneratedRegex(
        @"wan miniport|kernel debug network|teredo|isatap|6to4|ip-https|ras async|" +
        @"microsoft wi-fi direct|wi-fi direct virtual|packet scheduler|^sit\d|^dummy\d",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRx();

    private readonly Dictionary<string, (long Rx, long Tx, long Ticks)> _counters = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private bool _online;
    private int? _pingMs;
    private DateTime _lastReachabilityCheck = DateTime.MinValue;
    private PublicIpInfo? _publicIp;
    private volatile bool _publicIpRefreshing;

    /// <summary>
    /// Whether the user has asked for their public address at least once. Until
    /// they do, BBSpecs makes no lookup at all; afterwards it keeps the answer
    /// honest by re-checking when the connection changes.
    /// </summary>
    private bool _publicIpWanted;

    /// <summary>
    /// A cheap summary of "which way am I reaching the internet". When this
    /// changes — a VPN goes up or down, the address moves, Wi-Fi swaps to
    /// Ethernet — any public address already on screen is stale.
    /// </summary>
    private string _connectionFingerprint = "";

    // ---- platform hooks --------------------------------------------------------

    /// <summary>Called at the top of every collect so platforms can cache route state.</summary>
    protected virtual void BeginCollect() { }

    /// <summary>True when this adapter carries the machine's default route.</summary>
    protected abstract bool IsDefaultRoute(NetworkInterface nic);

    /// <summary>Details of the connected wireless link, or null if there isn't one.</summary>
    protected abstract WifiInfo? CollectWifi();

    // ---- collection ------------------------------------------------------------

    public NetworkInfo Collect()
    {
        var info = new NetworkInfo();

        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return info; }

        try { BeginCollect(); } catch { /* no route info this tick */ }

        AdapterInfo? routed = null;

        foreach (NetworkInterface nic in all)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (NoiseRx().IsMatch(nic.Description) || NoiseRx().IsMatch(nic.Name)) continue;

            AdapterInfo adapter = Describe(nic);
            try { adapter.IsPrimary = IsDefaultRoute(nic); } catch { }

            info.Adapters.Add(adapter);
            if (adapter.IsPrimary) routed = adapter;
        }

        // The default route often points at a VPN tunnel. That's worth knowing, but
        // it isn't the adapter the person plugged in — for "your network details"
        // we want the real Wi-Fi or Ethernet card underneath.
        AdapterInfo? physical = info.Adapters
            .Where(a => a.IsUp && !a.IsVirtual && !string.IsNullOrEmpty(a.Ipv4))
            .OrderByDescending(a => a.IsPrimary)
            .ThenByDescending(a => !string.IsNullOrEmpty(a.Gateway))
            .ThenByDescending(a => a.Kind == "Ethernet")
            .ThenByDescending(a => a.Kind == "Wi-Fi")
            .FirstOrDefault();

        info.Primary = physical ?? routed;
        if (info.Primary is not null) info.Primary.IsPrimary = true;

        info.ConnectionType = info.Primary?.Kind switch
        {
            "Wi-Fi" => "Wi-Fi",
            "Ethernet" => "Ethernet",
            "Mobile" => "Mobile",
            null => "Offline",
            _ => info.Primary.IsUp ? "Connected" : "Offline",
        };

        info.Wifi = Guard(CollectWifi);
        // A wireless link is the real connection even when a tunnel sits on top.
        if (info.Wifi is not null && info.ConnectionType is "Offline" or "Connected")
            info.ConnectionType = "Wi-Fi";

        info.Vpn = DetectVpn(all, routed);
        MeasureThroughput(info);
        CheckReachability();

        WatchForConnectionChange(info);

        info.Online = _online;
        info.PingMs = _pingMs;
        info.PublicIp = _publicIp;
        info.PublicIpRefreshing = _publicIpRefreshing;
        info.Verdict = Verdicts.RateConnection(
            info.ConnectionType, _online, info.Primary?.LinkSpeedMbps, _pingMs);

        return info;
    }

    /// <summary>
    /// Notices the connection changing and refreshes the public address, so it
    /// can't sit there claiming a VPN that has since been disconnected.
    /// </summary>
    private void WatchForConnectionChange(NetworkInfo info)
    {
        string fingerprint = string.Join('|',
            info.ConnectionType,
            info.Vpn.Active ? "vpn:" + info.Vpn.Name : "novpn",
            info.Primary?.Ipv4 ?? "noip",
            info.Primary?.Name ?? "noadapter");

        bool first = _connectionFingerprint.Length == 0;
        bool changed = !first && fingerprint != _connectionFingerprint;
        _connectionFingerprint = fingerprint;

        if (!changed || !_publicIpWanted || _publicIpRefreshing) return;

        // Only ever re-checked because the user opted in earlier by asking once.
        _publicIpRefreshing = true;
        _publicIp = null;
        _ = Task.Run(async () =>
        {
            // The new route needs a moment to settle before a lookup means anything.
            await Task.Delay(1500);
            await LookupPublicIpAsync();
        });
    }

    private static T? Guard<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch { return null; }
    }

    private static AdapterInfo Describe(NetworkInterface nic)
    {
        var a = new AdapterInfo
        {
            Name = nic.Name,
            Description = nic.Description,
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            Kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
                NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN / Tunnel",
                _ => "Other",
            },
        };

        bool looksLikeVpn = VpnRx().IsMatch(nic.Description) || VpnRx().IsMatch(nic.Name);
        a.IsVirtual = looksLikeVpn
                      || VirtualRx().IsMatch(nic.Description) || VirtualRx().IsMatch(nic.Name)
                      || a.Kind == "VPN / Tunnel";
        if (looksLikeVpn) a.Kind = "VPN / Tunnel";

        try
        {
            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length > 0) a.Mac = string.Join(':', mac.Select(b => b.ToString("X2")));
        }
        catch { /* some virtual adapters have no MAC */ }

        try
        {
            if (nic.Speed > 0) a.LinkSpeedMbps = nic.Speed / 1_000_000.0;
        }
        catch { /* not all drivers report a link speed */ }

        try
        {
            IPInterfaceProperties ip = nic.GetIPProperties();

            foreach (UnicastIPAddressInformation u in ip.UnicastAddresses)
            {
                if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    a.Ipv4 ??= u.Address.ToString();
                    try { a.SubnetMask ??= u.IPv4Mask?.ToString(); } catch { }
                }
                else if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                         && !u.Address.IsIPv6LinkLocal)
                {
                    a.Ipv6 ??= u.Address.ToString();
                }
            }

            a.Gateway = ip.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(g => g is not null && !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any))
                ?.ToString();

            a.Dns = ip.DnsAddresses.Select(d => d.ToString()).Take(4).ToList();

            try { a.DhcpEnabled = ip.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { }
        }
        catch { /* adapter went away mid-enumeration */ }

        return a;
    }

    /// <summary>
    /// A VPN counts as active only when its adapter is carrying the default
    /// route. Several clients leave their tunnel adapter installed, up, and
    /// still holding an address after you disconnect — checking only for its
    /// presence reports a VPN that is no longer routing anything, and keeps
    /// reporting it indefinitely.
    /// </summary>
    private static VpnInfo DetectVpn(NetworkInterface[] all, AdapterInfo? routed)
    {
        bool tunnelHasRoute = routed is not null
            && (VpnRx().IsMatch(routed.Description) || VpnRx().IsMatch(routed.Name)
                || routed.Kind == "VPN / Tunnel");

        if (!tunnelHasRoute)
        {
            return new VpnInfo
            {
                Active = false,
                Summary = "No VPN detected — you're connecting to the internet directly.",
            };
        }

        foreach (NetworkInterface nic in all)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            bool looksLikeVpn =
                nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                || VpnRx().IsMatch(nic.Description) || VpnRx().IsMatch(nic.Name);
            if (!looksLikeVpn) continue;

            // Of the tunnel adapters present, report the one actually in use.
            if (routed is not null && nic.Name != routed.Name) continue;

            // A VPN adapter that exists but holds no address isn't carrying traffic —
            // plenty of machines have an idle TAP adapter sitting around.
            bool hasAddress;
            try
            {
                hasAddress = nic.GetIPProperties().UnicastAddresses.Any(u =>
                    u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            }
            catch { continue; }
            if (!hasAddress) continue;

            string friendly = FriendlyVpnName(nic.Description, nic.Name);
            return new VpnInfo
            {
                Active = true,
                Name = friendly,
                Kind = nic.NetworkInterfaceType.ToString(),
                Summary = $"Connected through {friendly}. Your traffic is being routed through a VPN.",
            };
        }

        return new VpnInfo
        {
            Active = false,
            Summary = "No VPN detected — you're connecting to the internet directly.",
        };
    }

    private static string FriendlyVpnName(string description, string name)
    {
        (string needle, string label)[] known =
        [
            ("nordlynx", "NordVPN"), ("nordvpn", "NordVPN"),
            ("wireguard", "WireGuard"), ("openvpn", "OpenVPN"),
            ("tailscale", "Tailscale"), ("zerotier", "ZeroTier"),
            ("proton", "Proton VPN"), ("mullvad", "Mullvad"),
            ("expressvpn", "ExpressVPN"), ("surfshark", "Surfshark"),
            ("cyberghost", "CyberGhost"), ("private internet access", "Private Internet Access"),
            ("windscribe", "Windscribe"), ("hotspot shield", "Hotspot Shield"),
            ("anyconnect", "Cisco AnyConnect"), ("globalprotect", "Palo Alto GlobalProtect"),
            ("fortinet", "FortiClient"), ("fortissl", "FortiClient"),
            ("pulse secure", "Pulse Secure"), ("sonicwall", "SonicWall"),
            ("check point", "Check Point"), ("hamachi", "LogMeIn Hamachi"),
            ("radmin vpn", "Radmin VPN"), ("softether", "SoftEther"),
            ("tap-windows", "an OpenVPN-style tunnel"),
        ];

        string haystack = (description + " " + name).ToLowerInvariant();
        foreach ((string needle, string label) in known)
            if (haystack.Contains(needle, StringComparison.Ordinal)) return label;

        if (name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)) return "WireGuard";
        if (name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)) return "a VPN tunnel";

        return string.IsNullOrWhiteSpace(description) ? name : description;
    }

    /// <summary>Bytes/sec on the primary adapter, from counter deltas between ticks.</summary>
    private void MeasureThroughput(NetworkInfo info)
    {
        if (info.Primary is null) return;

        NetworkInterface? nic;
        try
        {
            nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name == info.Primary.Name && n.Description == info.Primary.Description);
        }
        catch { return; }
        if (nic is null) return;

        try
        {
            IPInterfaceStatistics stats = nic.GetIPStatistics();
            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;
            long now = Stopwatch.GetTimestamp();
            string key = nic.Id;

            if (_counters.TryGetValue(key, out (long Rx, long Tx, long Ticks) prev))
            {
                double seconds = (now - prev.Ticks) / (double)Stopwatch.Frequency;
                if (seconds <= 0.2) return;   // too soon to be meaningful

                // Counters wrap on 32-bit adapters; a negative delta means we rolled
                // over, so skip that sample rather than report nonsense.
                long dRx = rx - prev.Rx;
                long dTx = tx - prev.Tx;
                if (dRx >= 0) info.DownloadKbps = dRx / seconds / 1024.0;
                if (dTx >= 0) info.UploadKbps = dTx / seconds / 1024.0;
            }

            _counters[key] = (rx, tx, now);
        }
        catch { /* statistics unavailable on this adapter */ }
    }

    /// <summary>Pings a public resolver at most once every five seconds.</summary>
    private void CheckReachability()
    {
        if ((DateTime.UtcNow - _lastReachabilityCheck).TotalSeconds < 5) return;
        _lastReachabilityCheck = DateTime.UtcNow;

        try
        {
            using var ping = new Ping();
            PingReply reply = ping.Send(IPAddress.Parse("1.1.1.1"), 1200);
            _online = reply.Status == IPStatus.Success;
            _pingMs = _online ? (int)reply.RoundtripTime : null;
        }
        catch
        {
            // Unprivileged ICMP is blocked on plenty of Linux setups and firewalled
            // on plenty of networks — fall back to asking for a route out.
            try { _online = NetworkInterface.GetIsNetworkAvailable(); } catch { _online = false; }
            _pingMs = null;
        }
    }

    /// <summary>
    /// Where the public address lookup is asked, in order. Each free service has
    /// its own rate limit, and behind a VPN or a shared connection an exit address
    /// can already be over one through no fault of this machine — so the answer is
    /// to ask somewhere else rather than to give up.
    /// </summary>
    private static readonly string[] LookupServices =
    [
        "https://ipwho.is/",
        "https://ifconfig.co/json",
        "https://ipinfo.io/json",
        "https://api.ipify.org?format=json",
    ];

    /// <summary>
    /// Looks up the public IP and internet provider. Only ever called when the
    /// user clicks the button on the Internet tab — nothing leaves the machine
    /// otherwise.
    /// </summary>
    public async Task<PublicIpInfo> FetchPublicIpAsync()
    {
        // From here on, keep it current when the connection changes.
        _publicIpWanted = true;
        _publicIpRefreshing = true;
        return await LookupPublicIpAsync();
    }

    private async Task<PublicIpInfo> LookupPublicIpAsync()
    {
        var problems = new List<string>();

        foreach (string service in LookupServices)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, service);
                request.Headers.UserAgent.ParseAdd("BBSpecs");
                request.Headers.Accept.ParseAdd("application/json");

                using HttpResponseMessage response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    problems.Add($"{Host(service)} said {(int)response.StatusCode}");
                    continue;
                }

                PublicIpInfo? parsed = Parse(await response.Content.ReadAsStringAsync());
                if (parsed?.Ip is null or "")
                {
                    problems.Add($"{Host(service)} sent nothing usable");
                    continue;
                }

                _publicIp = parsed;
                _publicIpRefreshing = false;
                return _publicIp;
            }
            catch (Exception ex)
            {
                problems.Add($"{Host(service)}: {ex.Message}");
            }
        }

        _publicIp = new PublicIpInfo
        {
            Error = problems.Any(p => p.Contains("429", StringComparison.Ordinal))
                ? "The free lookup services are busy right now — this happens when lots of people " +
                  "share one address, which is normal on a VPN. Try again in a minute."
                : "Couldn't reach any lookup service. " + string.Join("; ", problems.Take(2)),
        };
        return _publicIp;
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    /// <summary>
    /// Reads whichever shape came back. The services agree on "ip" and disagree on
    /// everything else, so each alternative name is simply tried in turn.
    /// </summary>
    private static PublicIpInfo? Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Text(params string[] names)
            {
                foreach (string name in names)
                {
                    if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String)
                    {
                        string? value = e.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
                return null;
            }

            // ipwho.is nests the provider under "connection".
            string? nestedIsp = null;
            if (root.TryGetProperty("connection", out JsonElement connection)
                && connection.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in new[] { "isp", "org" })
                {
                    if (connection.TryGetProperty(name, out JsonElement e)
                        && e.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(e.GetString()))
                    {
                        nestedIsp = e.GetString();
                        break;
                    }
                }
            }

            return new PublicIpInfo
            {
                Ip = Text("ip", "ip_addr", "query"),
                Isp = nestedIsp ?? Text("org", "asn_org", "isp", "asn_organisation"),
                City = Text("city"),
                Region = Text("region", "region_name", "regionName"),
                Country = Text("country", "country_name", "country_iso"),
            };
        }
        catch
        {
            return null;
        }
    }

    // ---- helpers shared by both platform implementations ------------------------

    /// <summary>dBm to a 0-100 bar, using the conversion Windows itself uses.</summary>
    protected static int SignalPercentFromDbm(double dbm)
    {
        if (dbm <= -100) return 0;
        if (dbm >= -50) return 100;
        return (int)Math.Round(2 * (dbm + 100));
    }

    protected static string? BandFromFrequency(double? megahertz) => megahertz switch
    {
        null => null,
        < 2000 => null,
        < 2500 => "2.4 GHz",
        < 5925 => "5 GHz",
        <= 7125 => "6 GHz",
        _ => null,
    };
}
