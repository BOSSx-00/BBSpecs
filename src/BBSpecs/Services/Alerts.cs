using BBSpecs.Models;

namespace BBSpecs.Services;

/// <summary>
/// Decides when something is worth interrupting someone for.
///
/// The bar is deliberately high. A monitor that pops up every time a core
/// touches 80°C gets muted within a day, and then it is no use on the day
/// something is actually wrong. So: only a handful of conditions, each one
/// meaning real damage or real data loss, and each one has to hold for several
/// seconds before it counts. Nothing fires twice inside an hour, and a
/// condition has to properly clear before it can fire again.
/// </summary>
public sealed class Alerts
{
    public readonly record struct Alert(string Title, string Message, bool Warning);

    /// <summary>How long a reading must stay bad before it is believed.</summary>
    private const int RequiredReadings = 5;

    private static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    private sealed class Condition
    {
        public int Streak;
        public bool Firing;
        public DateTime LastFired = DateTime.MinValue;
    }

    private readonly Dictionary<string, Condition> _conditions = [];

    /// <summary>
    /// Looks at a reading and returns anything that has just become worth
    /// saying. Called once per snapshot; usually returns nothing at all.
    /// </summary>
    public List<Alert> Check(Snapshot s)
    {
        var raised = new List<Alert>();

        if (s.Cpu.TempC is double cpuTemp)
        {
            Consider(raised, "cpu-temp", cpuTemp >= 95,
                "Processor is running hot",
                $"Your CPU has been at {cpuTemp:0}°C for a few seconds. It will slow itself down to avoid damage. " +
                "Closing what you are running, or clearing the dust out of the cooler, usually fixes this.",
                warning: true);
        }

        foreach (GpuInfo gpu in s.Gpus)
        {
            if (gpu.TempC is not double gpuTemp) continue;

            Consider(raised, $"gpu-temp-{gpu.Name}", gpuTemp >= 90,
                "Graphics card is running hot",
                $"{gpu.Name} has been at {gpuTemp:0}°C for a few seconds. Check the fans are spinning and that " +
                "the vents are not blocked.",
                warning: true);
        }

        foreach (Models.DriveInfo drive in s.Drives)
        {
            if (drive.HealthPercent is double life)
            {
                Consider(raised, $"drive-life-{drive.Index}", life < 10,
                    "A drive is wearing out",
                    $"{drive.Model} reports {life:0}% of its rated life left. Back up anything on it that matters. " +
                    "Drives usually give plenty of warning, but not always.",
                    warning: true);
            }

            foreach (VolumeInfo volume in drive.Volumes)
            {
                if (volume.TotalGb <= 0) continue;

                double freePercent = volume.FreeGb / volume.TotalGb * 100;
                Consider(raised, $"volume-full-{volume.Letter}", freePercent < 3 && volume.IsSystem,
                    "Windows is running out of space",
                    $"{volume.Letter} has {volume.FreeGb:0.#} GB left. Below this, Windows updates start failing " +
                    "and the whole machine slows down.",
                    warning: true);
            }
        }

        Consider(raised, "offline", !s.Network.Online,
            "Internet connection lost",
            "BBSpecs cannot reach the internet. If nothing changed at your end, it is probably your provider.",
            warning: false);

        return raised;
    }

    /// <summary>Clears the history, so turning alerts back on starts fresh.</summary>
    public void Reset() => _conditions.Clear();

    private void Consider(List<Alert> raised, string key, bool bad, string title, string message, bool warning)
    {
        if (!_conditions.TryGetValue(key, out Condition? condition))
            _conditions[key] = condition = new Condition();

        if (!bad)
        {
            // Back to normal. Only now can this condition speak up again.
            condition.Streak = 0;
            condition.Firing = false;
            return;
        }

        condition.Streak++;
        if (condition.Firing || condition.Streak < RequiredReadings) return;

        if (DateTime.UtcNow - condition.LastFired < Quiet)
        {
            // Still inside the quiet hour. Mark it as firing anyway, so it does
            // not queue up and arrive the moment the hour is over.
            condition.Firing = true;
            return;
        }

        condition.Firing = true;
        condition.LastFired = DateTime.UtcNow;
        raised.Add(new Alert(title, message, warning));
    }
}
