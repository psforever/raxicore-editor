using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using RaxicoreEditor.Editor.Updates;

namespace RaxicoreEditor.Editor.Views
{
    /// <summary>
    /// Offers a discovered <see cref="UpdateInfo"/> to the user. Nothing is downloaded until they click
    /// "Download &amp; Install" — that click is the explicit consent to fetch and run the installer. On
    /// install, the app downloads the NSIS installer, launches it, and shuts down so the installer can
    /// replace the running files (its finish page relaunches the updated app).
    /// </summary>
    public partial class UpdateDialog : Window
    {
        private readonly UpdateInfo _info;
        private bool _installing;

        // Avalonia needs a parameterless ctor for XAML tooling; not used at runtime.
        public UpdateDialog() : this(new UpdateInfo(new Version(0, 0), "", "", "", "", "", 0, "")) { }

        public UpdateDialog(UpdateInfo info)
        {
            _info = info;
            InitializeComponent();

            HeadlineText.Text = $"Raxicore Editor {FormatVersion(info.Version)} is available";
            CurrentText.Text = $"You have {AppVersion.Display}.";
            NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
                ? "No release notes were provided."
                : info.Notes.Trim();
        }

        private async void OnInstall(object? sender, RoutedEventArgs e)
        {
            if (_installing)
            {
                return;
            }
            _installing = true;
            InstallButton.IsEnabled = false;
            SkipButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
            ProgressPanel.IsVisible = true;
            ProgressText.Text = "Starting download…";

            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p;
                ProgressText.Text = $"Downloading… {p * 100:0}%";
            });

            try
            {
                string installerPath = await UpdateService.DownloadAsync(_info, progress);
                ProgressText.Text = "Launching installer…";
                UpdateService.LaunchInstaller(installerPath);

                // Give the installer a moment to spin up, then exit so it can overwrite our files.
                await Task.Delay(400);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                // Re-enable so the user can retry or dismiss.
                _installing = false;
                InstallButton.IsEnabled = true;
                SkipButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                ProgressText.Text = "Update failed: " + ex.Message;
            }
        }

        private void OnSkip(object? sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.Settings.SkippedUpdateVersion = _info.Version.ToString();
                app.Settings.Save();
            }
            Close();
        }

        private void OnLater(object? sender, RoutedEventArgs e) => Close();

        // Show a 4-part version compactly: 0.1.1.0 -> "0.1.1", 0.2.0.0 -> "0.2".
        private static string FormatVersion(Version v)
        {
            if (v.Revision > 0)
            {
                return v.ToString(4);
            }
            return v.Build > 0 ? v.ToString(3) : v.ToString(2);
        }
    }
}
