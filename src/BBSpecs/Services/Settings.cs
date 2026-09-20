using System.Text.Json;

namespace BBSpecs.Services;

/// <summary>
/// The handful of choices that should outlive a restart: the theme, whether
/// readings are masked, and whether desktop alerts are on.
///
/// Kept beside the app's own data rather than in the page, because a webview
/// loaded from file:// gets an origin that browsers treat as untrustworthy, and
/// its localStorage can be wiped or refused without warning. A small JSON file
/// is boring and always works.
/// </summary>
public sealed class Settings
{
    public string Theme { get; set; } = "bossx";
    public bool Privacy { get; set; } = true;
    public bool Notifications { get; set; }

    /// <summary>
    /// Whether the first-run greeting has been shown. Kept with the settings
    /// rather than beside the program, so it survives an update, a move, and
    /// the difference between the installed and portable builds.
    /// </summary>
    public bool SeenWelcome { get; set; }

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "BBSpecs", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                Settings loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options)
                                  ?? new Settings();

                // Themes that were renamed or withdrawn. Without this, anybody
                // who had one chosen would silently find themselves back on the
                // default with no idea why.
                string moved = loaded.Theme switch
                {
                    "barbie" => "akbu",
                    "astro2" => "astro",
                    "astro3" => "astro",
                    _ => loaded.Theme,
                };

                if (moved != loaded.Theme)
                {
                    loaded.Theme = moved;
                    loaded.Save();
                }

                return loaded;
            }
        }
        catch { /* a corrupt or unreadable file is not worth failing the launch over */ }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch { /* read-only profile, roaming hiccup: not worth interrupting anyone */ }
    }

    /// <summary>
    /// Applies one change from the page. Values are checked here rather than
    /// trusted, so a malformed message can't put the file into a state that
    /// makes the next launch render with no theme at all.
    /// </summary>
    public void Apply(string key, JsonElement value)
    {
        switch (key)
        {
            case "theme":
                if (value.ValueKind == JsonValueKind.String
                    && value.GetString() is string theme
                    && theme is "bossx" or "akbu" or "astro")
                {
                    Theme = theme;
                }
                break;

            case "privacy":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Privacy = value.GetBoolean();
                break;

            case "notifications":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Notifications = value.GetBoolean();
                break;

            case "seenWelcome":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    SeenWelcome = value.GetBoolean();
                break;

            default:
                return;
        }

        Save();
    }
}
