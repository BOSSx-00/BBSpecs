using BBSpecs.Models;

namespace BBSpecs.Services;

/// <summary>
/// Mounted-volume readings. System.IO.DriveInfo works the same on Windows and
/// Linux, so once each platform has worked out which mount points belong to which
/// physical drive, the sizes come from one shared place.
/// </summary>
public static class Volumes
{
    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>Windows: builds volumes from drive letters such as C, D, E.</summary>
    public static List<VolumeInfo> Build(IEnumerable<char> letters, string systemLetter)
    {
        var volumes = new List<VolumeInfo>();

        foreach (char letter in letters.Distinct().OrderBy(c => c))
        {
            string id = letter + ":";
            VolumeInfo? v = Read(id + Path.DirectorySeparatorChar, id, id == systemLetter);
            if (v is not null) volumes.Add(v);
        }

        return volumes;
    }

    /// <summary>Linux: builds volumes from mount points such as /, /home, /mnt/data.</summary>
    public static List<VolumeInfo> BuildFromMounts(IEnumerable<string> mountPoints)
    {
        var volumes = new List<VolumeInfo>();

        foreach (string mount in mountPoints.Distinct().OrderBy(m => m, StringComparer.Ordinal))
        {
            VolumeInfo? v = Read(mount, mount, mount == "/");
            if (v is not null) volumes.Add(v);
        }

        return volumes;
    }

    private static VolumeInfo? Read(string path, string display, bool isSystem)
    {
        try
        {
            var sys = new System.IO.DriveInfo(path);
            if (!sys.IsReady) return null;

            double total = sys.TotalSize / Gb;
            if (total <= 0) return null;

            double free = sys.TotalFreeSpace / Gb;

            return new VolumeInfo
            {
                Letter = display,
                Label = string.IsNullOrWhiteSpace(sys.VolumeLabel) ? null : sys.VolumeLabel,
                FileSystem = string.IsNullOrWhiteSpace(sys.DriveFormat) ? null : sys.DriveFormat,
                TotalGb = Math.Round(total, 1),
                FreeGb = Math.Round(free, 1),
                UsedGb = Math.Round(total - free, 1),
                UsedPercent = Math.Round((total - free) / total * 100.0, 1),
                IsSystem = isSystem,
            };
        }
        catch
        {
            // BitLocker-locked, unmounted between calls, or otherwise unreadable.
            return null;
        }
    }

    /// <summary>Re-reads free space. Called every tick: it's the number people watch.</summary>
    public static void RefreshSpace(List<VolumeInfo> volumes)
    {
        foreach (VolumeInfo v in volumes)
        {
            string path = v.Letter.EndsWith(':') ? v.Letter + Path.DirectorySeparatorChar : v.Letter;
            try
            {
                var sys = new System.IO.DriveInfo(path);
                if (!sys.IsReady) continue;

                v.TotalGb = Math.Round(sys.TotalSize / Gb, 1);
                v.FreeGb = Math.Round(sys.TotalFreeSpace / Gb, 1);
                v.UsedGb = Math.Round(v.TotalGb - v.FreeGb, 1);
                v.UsedPercent = v.TotalGb > 0 ? Math.Round(v.UsedGb / v.TotalGb * 100.0, 1) : 0;
            }
            catch { /* volume disappeared or isn't accessible */ }
        }
    }

    public static Verdict RateTotal(List<VolumeInfo> volumes)
    {
        double total = volumes.Sum(v => v.TotalGb);
        double free = volumes.Sum(v => v.FreeGb);
        return total > 0
            ? Verdicts.RateSpace(free, total)
            : Verdict.Unknown("No formatted volumes on this drive.");
    }
}
