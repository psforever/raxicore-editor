using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RaxicoreEditor.Editor.Views
{
    /// <summary>Small modal "About" dialog: app icon, name, version (from <see cref="AppVersion"/>),
    /// a link to the project, and the copyright line.</summary>
    public partial class AboutDialog : Window
    {
        private const string RepoUrl = "https://github.com/psforever/raxicore-editor";

        public AboutDialog()
        {
            InitializeComponent();
            VersionText.Text = string.IsNullOrEmpty(AppVersion.Display)
                ? "Version unknown"
                : "Version " + AppVersion.Display;
            CopyrightText.Text = "Copyright © 2026 Raxicore Editor contributors";
        }

        private async void OnOpenRepo(object? sender, RoutedEventArgs e)
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri(RepoUrl));
            }
            catch
            {
                // Opening the browser is best-effort; a failure here shouldn't disturb the dialog.
            }
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
