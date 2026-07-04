using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ShadTheme = ShadUI.ShadTheme;

namespace RaxicoreEditor.Editor.Theming
{
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
            }
        }

        private void Set(string key, uint argb)
        {
            if (_app.Resources.TryGetResource(key, null, out object? res) && res is SolidColorBrush brush)
            {
                brush.Color = Color.FromUInt32(argb);
            }
        }
    }
}
