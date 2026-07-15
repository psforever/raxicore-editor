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

        /// <summary>Whether continents draw their procedural sky (see <see cref="RenderSettings.Sky"/>).</summary>
        public bool Sky { get; set; } = false;

        /// <summary>Viewport framerate cap in FPS (see <see cref="RenderSettings.FrameCap"/>). <c>0</c> =
        /// uncapped. Default uncapped so the viewport runs at the display's refresh out of the box.</summary>
        public int FrameRateCap { get; set; } = 0;

        /// <summary>Explicit path to a <c>blender</c> executable for .blend export. Empty = auto-detect
        /// (PATH, then the standard install locations).</summary>
        public string BlenderPath { get; set; } = "";

        /// <summary>Whether the app checks GitHub for a newer release at startup. The check only ever
        /// *offers* an update — downloading and installing always require an explicit click. On by default.</summary>
        public bool AutoCheckForUpdates { get; set; } = true;

        /// <summary>A release version the user chose to skip (e.g. <c>0.1.1.0</c>); the startup check won't
        /// re-prompt for it. Empty = nothing skipped. Cleared implicitly once a newer version appears.</summary>
        public string SkippedUpdateVersion { get; set; } = "";

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
