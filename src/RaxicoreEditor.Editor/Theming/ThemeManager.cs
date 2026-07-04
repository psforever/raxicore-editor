using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ShadTheme = ShadUI.ShadTheme;

namespace RaxicoreEditor.Editor.Theming
{
    /// <summary>Named colour scheme for the status bar (background + loading-bar accent).</summary>
    public enum StatusBarTheme
    {
        Tradition,   // red / grey
        Liberty,     // blue / gold
        Technology,  // violet / teal
        Disruption,  // army-green / cyan-teal
    }

    /// <summary>
    /// Owns the active application theme (ShadUI / shadcn) and the chrome brush palette. The light/dark
    /// variant follows the OS by default and can be overridden via <see cref="SetVariant"/>.
    /// </summary>
    public sealed class ThemeManager
    {
        private readonly Application _app;
        private readonly ShadTheme _shad = new();

        public ThemeManager(Application app)
        {
            _app = app;
            _app.ActualThemeVariantChanged += (_, _) => ApplyChrome();
        }

        /// <summary>Install the ShadUI theme and sync the chrome palette. Call once at startup.</summary>
        public void Initialize()
        {
            if (_app.Styles.Count == 0)
            {
                _app.Styles.Add(_shad);
            }
            else
            {
                _app.Styles[0] = _shad;
            }
            ApplyChrome();
        }

        /// <summary>Set the light/dark variant. Pass <see cref="ThemeVariant.Default"/> to follow the OS.</summary>
        public void SetVariant(ThemeVariant variant)
        {
            _app.RequestedThemeVariant = variant;
            ApplyChrome();
        }

        /// <summary>The status-bar colour theme currently applied.</summary>
        public StatusBarTheme StatusBar { get; private set; } = StatusBarTheme.Technology;

        /// <summary>
        /// Apply a named status-bar theme (background + loading-bar accent). Independent of the light/dark
        /// variant, so the status bar keeps its saturated empire colours in both.
        /// </summary>
        public void SetStatusBarTheme(StatusBarTheme theme)
        {
            StatusBar = theme;
            (uint background, uint loading) = theme switch
            {
                StatusBarTheme.Tradition  => (0xFF741D15u, 0xFF2C2C2Cu), // red / grey
                StatusBarTheme.Liberty    => (0xFF1D4E9Au, 0xFFEEC53Au), // blue / gold
                StatusBarTheme.Technology => (0xFF532177u, 0xFF385E6Au), // violet / teal
                StatusBarTheme.Disruption => (0xFF27312Fu, 0xFF3F6270u), // army-green / cyan-teal
                _                         => (0xFF532177u, 0xFF385E6Au),
            };
            Set("AccentPrimaryBrush", background);     // status-bar background + section headers
            Set("AccentSecondaryBrush", loading);      // loading bar + asset/file selection highlight
        }

        private void ApplyChrome()
        {
            // shadcn-style zinc neutrals so our custom panels match the ShadUI controls.
            bool dark = _app.ActualThemeVariant == ThemeVariant.Dark;
            if (dark)
            {
                Set("WindowBackgroundBrush", 0xFF09090B); // background (zinc-950)
                Set("PanelBackgroundBrush", 0xFF18181B);  // card (zinc-900)
                Set("EditorBackgroundBrush", 0xFF0C0C0E);
                Set("ViewportBackgroundBrush", 0xFF0A0A0C);
                Set("SeparatorBrush", 0xFF27272A);        // border (zinc-800)
                Set("TextBrush", 0xFFFAFAFA);             // foreground
                Set("SubtleTextBrush", 0xFFA1A1AA);       // muted (zinc-400)
                Set("AccentBrush", 0xFF60A5FA);
                // Darker accents in dark mode (red-700 / blue-700 / violet-700).
                Set("AccentRedBrush", 0xFFB91C1C);
                Set("AccentBlueBrush", 0xFF1D4ED8);
                Set("AccentPurpleBrush", 0xFF6D28D9);
                SetColor("AccentRedColor", 0xFFB91C1C);
                SetColor("AccentBlueColor", 0xFF1D4ED8);
                SetColor("AccentPurpleColor", 0xFF6D28D9);
                // Title bar: very dark, subtle deep tints (blue/violet/red-950) over the dark chrome.
                SetColor("TitleBarBlueColor", 0xFF172554);
                SetColor("TitleBarPurpleColor", 0xFF2E1065);
                SetColor("TitleBarRedColor", 0xFF450A0A);
                // Light shadow behind accent-coloured text in dark mode.
                SetColor("AccentTextShadowColor", 0x99FFFFFF);
            }
            else
            {
                Set("WindowBackgroundBrush", 0xFFFFFFFF);
                Set("PanelBackgroundBrush", 0xFFF4F4F5);  // zinc-100
                Set("EditorBackgroundBrush", 0xFFFFFFFF);
                Set("ViewportBackgroundBrush", 0xFFE4E4E7);
                Set("SeparatorBrush", 0xFFE4E4E7);        // zinc-200
                Set("TextBrush", 0xFF09090B);
                Set("SubtleTextBrush", 0xFF71717A);       // zinc-500
                Set("AccentBrush", 0xFF2563EB);
                // Lighter accents in light mode (red-400 / blue-400 / violet-400).
                Set("AccentRedBrush", 0xFFF87171);
                Set("AccentBlueBrush", 0xFF60A5FA);
                Set("AccentPurpleBrush", 0xFFA78BFA);
                SetColor("AccentRedColor", 0xFFF87171);
                SetColor("AccentBlueColor", 0xFF60A5FA);
                SetColor("AccentPurpleColor", 0xFFA78BFA);
                // Title bar: very light, subtle pastels (blue/violet/red-100) over the light chrome.
                SetColor("TitleBarBlueColor", 0xFFDBEAFE);
                SetColor("TitleBarPurpleColor", 0xFFEDE9FE);
                SetColor("TitleBarRedColor", 0xFFFEE2E2);
                // Dark shadow behind accent-coloured text in light mode.
                SetColor("AccentTextShadowColor", 0x99000000);
            }
        }

        private void Set(string key, uint argb)
        {
            if (_app.Resources.TryGetResource(key, null, out object? res) && res is SolidColorBrush brush)
            {
                brush.Color = Color.FromUInt32(argb);
            }
        }

        // Color resources are value types, so replace the entry (DynamicResource consumers re-resolve).
        private void SetColor(string key, uint argb) => _app.Resources[key] = Color.FromUInt32(argb);
    }
}
