using System.Text;
using BBSpecs.Models;
using DriveInfo = BBSpecs.Models.DriveInfo;

namespace BBSpecs.Services;

/// <summary>
/// The plain-text summary behind the Copy Specs button: the thing you paste into
/// a forum post, a support chat, or a message to whoever you ask for PC help.
/// Deliberately short and free of serial numbers, addresses and anything else
/// that shouldn't be pasted into a public thread.
/// </summary>
public static class SpecsText
{
    public static string Build(Snapshot s)
    {
        var text = new StringBuilder();

        text.Append("CPU: ").AppendLine(Cpu(s.Cpu));
        text.Append("GPU: ").AppendLine(Gpu(s.Gpus));
        text.Append("Motherboard: ").AppendLine(Board(s.Board));
        text.Append("RAM: ").AppendLine(Memory(s.Memory));
        text.Append("Storage: ").AppendLine(Storage(s.Drives));
        text.Append("Internet: ").AppendLine(Internet(s.Network));

        return text.ToString();
    }

    private static string Cpu(CpuInfo cpu)
    {
        if (string.IsNullOrWhiteSpace(cpu.Name)) return "Unknown";

        string cores = cpu.PhysicalCores > 0
            ? $" ({cpu.PhysicalCores} cores / {cpu.LogicalProcessors} threads)"
            : "";
        return cpu.Name + cores;
    }

    private static string Gpu(List<GpuInfo> gpus)
    {
        if (gpus.Count == 0) return "Unknown";

        // Dedicated card first, integrated noted after it so the list is honest
        // without burying the part that matters.
        IEnumerable<string> described = gpus.Select(g =>
            g.VramTotalGb is double vram and > 0
                ? $"{g.Name} ({vram:0.#} GB)"
                : g.Name);

        return string.Join(" + ", described);
    }

    private static string Board(BoardInfo board)
    {
        string name = $"{board.Manufacturer} {board.Product}".Trim();
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";

        return string.IsNullOrWhiteSpace(board.BiosVersion)
            ? name
            : $"{name} (BIOS {board.BiosVersion})";
    }

    private static string Memory(MemoryInfo memory)
    {
        if (memory.TotalGb <= 0) return "Unknown";

        var parts = new List<string> { $"{memory.TotalGb:0.#} GB" };
        if (!string.IsNullOrEmpty(memory.Type)) parts.Add(memory.Type);
        if (memory.SpeedMhz is int speed and > 0) parts.Add($"{speed} MHz");
        if (memory.SlotsUsed > 0) parts.Add($"{memory.SlotsUsed} of {memory.SlotsTotal} slots");

        return string.Join(" ", parts);
    }

    private static string Storage(List<DriveInfo> drives)
    {
        if (drives.Count == 0) return "Unknown";

        IEnumerable<string> described = drives.Select(d =>
        {
            string size = d.SizeGb >= 1024 ? $"{d.SizeGb / 1024:0.#} TB" : $"{d.SizeGb:0} GB";
            string tag = d.IsSystemDrive ? ", Windows drive" : "";
            return $"{d.Model}: {size} {d.Kind}{tag}";
        });

        // One per line, indented, so a four-drive machine still reads cleanly.
        return Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", described);
    }

    private static string Internet(NetworkInfo network)
    {
        AdapterInfo? primary = network.Primary;
        if (primary is null) return "Not connected";

        string hardware = string.IsNullOrWhiteSpace(primary.Description) ? primary.Name : primary.Description;

        if (network.Wifi is not null)
        {
            // The network's name is the user's business, so it stays out of this.
            var bits = new List<string> { hardware };
            if (!string.IsNullOrEmpty(network.Wifi.RadioType)) bits.Add(network.Wifi.RadioType);
            if (!string.IsNullOrEmpty(network.Wifi.Band)) bits.Add(network.Wifi.Band);
            return string.Join(", ", bits);
        }

        return primary.LinkSpeedMbps is double mbps and > 0
            ? $"{hardware} ({Verdicts.FormatLinkSpeed(mbps)} wired)"
            : hardware;
    }
}
