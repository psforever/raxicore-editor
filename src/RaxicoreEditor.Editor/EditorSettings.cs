using System;
using System.IO;
using System.Text.Json;

namespace RaxicoreEditor.Editor
{
    /// <summary>
    /// User preferences persisted between sessions, stored as JSON under
    /// <c>%APPDATA%\RaxicoreEditor\settings.json</c>. Currently just the status-bar colour theme.
    /// </summary>
    public sealed class EditorSettings
    {
        /// <summary>Selected status-bar theme name: Tradition | Liberty | Technology | Disruption.</summary>
        public string StatusBarTheme { get; set; } = "Technology";

        /// <summary>Selected model detail tier: Detailed | Low.</summary>
        public string ModelDetail { get; set; } = "Detailed";

        /// <summary>Whether the viewport draws with the engine-derived material shaders (see
        /// <see cref="RenderSettings.EngineShading"/>). Off by default.</summary>
        public bool EngineShading { get; set; } = false;

        /// <summary>Explicit path to a <c>blender</c> executable for .blend export. Empty = auto-detect
        /// (PATH, then the standard install locations).</summary>
        public string BlenderPath { get; set; } = "";

        private static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaxicoreEditor");

        private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

        /// <summary>Read settings from disk, falling back to defaults if missing or unreadable.</summary>
        public static EditorSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(SettingsPath)) ?? new EditorSettings();
                }
            }
            catch { /* corrupt / unreadable settings just fall back to defaults */ }
            return new EditorSettings();
        }

        /// <summary>Best-effort write of the current settings to disk (IO errors are ignored).</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort persistence */ }
        }
    }
}
