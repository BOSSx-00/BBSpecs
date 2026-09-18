using System.Runtime.InteropServices;
using System.Text;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Minimal binding to the native Wi-Fi API. We use this rather than parsing
/// `netsh wlan show interfaces` because netsh's output is localised — on a German
/// or French Windows the labels change and the parse silently produces nothing.
/// </summary>
internal static class WlanApi
{
    // ---- structures ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public uint isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;   // 0-100
        public uint ulRxRate;            // Kbps
        public uint ulTxRate;            // Kbps
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public uint isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    private const uint OPCODE_CURRENT_CONNECTION = 7;
    private const uint OPCODE_CHANNEL_NUMBER = 8;
    private const uint INTERFACE_STATE_CONNECTED = 1;

    // ---- imports -------------------------------------------------------------

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
        IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    // ---- public surface ------------------------------------------------------

    public sealed record WlanConnection(
        Guid InterfaceGuid,
        string InterfaceDescription,
        string Ssid,
        string Bssid,
        int SignalPercent,
        int PhyType,
        double RxMbps,
        double TxMbps,
        int AuthAlgorithm,
        int CipherAlgorithm,
        string ProfileName,
        int? Channel);

    /// <summary>The currently connected wireless network, or null if there isn't one.</summary>
    public static WlanConnection? GetCurrentConnection()
    {
        IntPtr client = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        try
        {
            // Client version 2 covers Vista and newer.
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0) return null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return null;

            int count = Marshal.ReadInt32(list, 0);
            int entrySize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < count; i++)
            {
                IntPtr entry = IntPtr.Add(list, 8 + (i * entrySize));
                WLAN_INTERFACE_INFO info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entry);
                if (info.isState != INTERFACE_STATE_CONNECTED) continue;

                Guid guid = info.InterfaceGuid;
                IntPtr data = IntPtr.Zero;
                try
                {
                    if (WlanQueryInterface(client, ref guid, OPCODE_CURRENT_CONNECTION,
                            IntPtr.Zero, out _, out data, IntPtr.Zero) != 0 || data == IntPtr.Zero)
                        continue;

                    WLAN_CONNECTION_ATTRIBUTES conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                    WLAN_ASSOCIATION_ATTRIBUTES assoc = conn.wlanAssociationAttributes;

                    string ssid = assoc.dot11Ssid.ucSSID is null
                        ? ""
                        : Encoding.UTF8.GetString(assoc.dot11Ssid.ucSSID, 0,
                            Math.Min((int)assoc.dot11Ssid.uSSIDLength, 32)).TrimEnd('\0');

                    string bssid = assoc.dot11Bssid is null
                        ? ""
                        : string.Join(':', assoc.dot11Bssid.Select(b => b.ToString("X2")));

                    return new WlanConnection(
                        guid,
                        info.strInterfaceDescription,
                        ssid,
                        bssid,
                        (int)assoc.wlanSignalQuality,
                        (int)assoc.dot11PhyType,
                        assoc.ulRxRate / 1000.0,
                        assoc.ulTxRate / 1000.0,
                        (int)conn.wlanSecurityAttributes.dot11AuthAlgorithm,
                        (int)conn.wlanSecurityAttributes.dot11CipherAlgorithm,
                        conn.strProfileName ?? "",
                        QueryChannel(client, ref guid));
                }
                finally
                {
                    if (data != IntPtr.Zero) WlanFreeMemory(data);
                }
            }
        }
        catch
        {
            // No wireless service, no adapter, or a driver that doesn't answer —
            // the Internet tab simply shows no Wi-Fi section.
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
        return null;
    }

    private static int? QueryChannel(IntPtr client, ref Guid guid)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            if (WlanQueryInterface(client, ref guid, OPCODE_CHANNEL_NUMBER,
                    IntPtr.Zero, out uint size, out data, IntPtr.Zero) != 0) return null;
            if (data == IntPtr.Zero || size < 4) return null;
            int channel = Marshal.ReadInt32(data);
            return channel > 0 ? channel : null;
        }
        catch { return null; }
        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
    }

    /// <summary>802.11 PHY type to the name people actually recognise.</summary>
    public static string RadioName(int phyType) => phyType switch
    {
        1 => "802.11 FHSS",
        2 => "802.11 DSSS",
        3 => "802.11 Infrared",
        4 => "Wi-Fi 2 (802.11a)",
        5 => "Wi-Fi 1 (802.11b)",
        6 => "Wi-Fi 3 (802.11g)",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        9 => "WiGig (802.11ad)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "Wi-Fi",
    };

    public static string AuthName(int algorithm) => algorithm switch
    {
        1 => "Open (no password)",
        2 => "WEP (shared key)",
        3 => "WPA Enterprise",
        4 => "WPA Personal",
        5 => "WPA None",
        6 => "WPA2 Enterprise",
        7 => "WPA2 Personal",
        8 => "WPA3 Enterprise 192-bit",
        9 => "WPA3 Personal",
        10 => "Enhanced Open (OWE)",
        11 => "WPA3 Enterprise",
        _ => "Unknown",
    };

    public static string CipherName(int algorithm) => algorithm switch
    {
        0x00 => "None",
        0x01 => "WEP-40",
        0x02 => "TKIP",
        0x04 => "AES (CCMP)",
        0x05 => "WEP-104",
        0x06 => "BIP",
        0x08 => "GCMP",
        0x09 => "GCMP-256",
        0x0a => "AES (CCMP-256)",
        0x100 => "WPA Use Group",
        0x101 => "WEP",
        _ => "Unknown",
    };

    /// <summary>
    /// Channel numbers repeat across bands, so this is a best guess: everything at
    /// or below 14 is 2.4 GHz, up to 177 is 5 GHz, and anything higher is 6 GHz.
    /// </summary>
    public static string? BandFromChannel(int? channel) => channel switch
    {
        null => null,
        <= 0 => null,
        <= 14 => "2.4 GHz",
        <= 177 => "5 GHz",
        _ => "6 GHz",
    };
}
