using System;
using Avalonia;

namespace RaxicoreEditor.Editor
{
    /// <summary>Application entry point. No top-level statements — explicit <see cref="Main"/>.</summary>
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Used by the Avalonia designer and by Main().
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
    }
}
