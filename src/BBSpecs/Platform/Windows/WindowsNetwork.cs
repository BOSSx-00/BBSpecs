using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BBSpecs.Models;
using BBSpecs.Services;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// The two Windows-specific pieces of the network picture: which adapter Windows
/// would actually route through, and the wireless link details from the native
/// Wi-Fi API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetwork : NetworkService
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    private uint _bestIndex;

    protected override void BeginCollect()
    {
        try
        {
            // 8.8.8.8 reads the same in either byte order, so endianness doesn't bite.
            _bestIndex = GetBestInterface(0x08080808, out uint index) == 0 ? index : 0;
        }
        catch { _bestIndex = 0; }
    }

    protected override bool IsDefaultRoute(NetworkInterface nic)
    {
        if (_bestIndex == 0) return false;

        try
        {
            IPInterfaceProperties ip = nic.GetIPProperties();

            int? index = null;
            try { index = ip.GetIPv4Properties()?.Index; } catch { }
            if (index is null) { try { index = ip.GetIPv6Properties()?.Index; } catch { } }

            return index == (int)_bestIndex;
        }
        catch { return false; }
    }

    protected override WifiInfo? CollectWifi()
    {
        WlanApi.WlanConnection? conn = WlanApi.GetCurrentConnection();
        if (conn is null || string.IsNullOrEmpty(conn.Ssid)) return null;

        string? band = WlanApi.BandFromChannel(conn.Channel);
        string radio = WlanApi.RadioName(conn.PhyType);

        var wifi = new WifiInfo
        {
            Ssid = conn.Ssid,
            Bssid = string.IsNullOrEmpty(conn.Bssid) ? null : conn.Bssid,
            SignalPercent = conn.SignalPercent,
            SignalLabel = Verdicts.SignalLabel(conn.SignalPercent),
            RadioType = radio,
            Band = band,
            Channel = conn.Channel,
            Security = WlanApi.AuthName(conn.AuthAlgorithm),
            Cipher = WlanApi.CipherName(conn.CipherAlgorithm),
            ReceiveMbps = conn.RxMbps > 0 ? conn.RxMbps : null,
            TransmitMbps = conn.TxMbps > 0 ? conn.TxMbps : null,
            Profile = string.IsNullOrWhiteSpace(conn.ProfileName) ? null : conn.ProfileName,
        };

        wifi.Verdict = Verdicts.RateWifi(wifi.SignalPercent, radio, band);
        return wifi;
    }
}
