using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RaxicoreEditor.Editor.Theming;
using RaxicoreEditor.Editor.Views;

namespace RaxicoreEditor.Editor
{
    public partial class App : Application
    {
        public ThemeManager Themes { get; private set; } = null!;

        /// <summary>Persisted user preferences (status-bar theme, …). Saved on change.</summary>
        public EditorSettings Settings { get; private set; } = null!;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            Themes = new ThemeManager(this);
            Themes.Initialize(); // ShadUI (shadcn) theme at Styles[0]; ShadTheme also styles DataGrid.

            Settings = EditorSettings.Load();
            Themes.SetStatusBarTheme(
                Enum.TryParse(Settings.StatusBarTheme, out StatusBarTheme saved) ? saved : StatusBarTheme.Technology);
            RenderSettings.Detail =
                Enum.TryParse(Settings.ModelDetail, out ModelDetail detail) ? detail : ModelDetail.Detailed;
            RenderSettings.EngineShading = Settings.EngineShading;
            RenderSettings.Sky = Settings.Sky;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
