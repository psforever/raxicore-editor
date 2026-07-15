using System;
using System.Reflection;

namespace RaxicoreEditor.Editor
{
    /// <summary>The application's version, read from the assembly metadata set in
    /// <c>Directory.Build.props</c> (the single source of truth). <see cref="Display"/> is the friendly
    /// string (e.g. <c>0.1.0 Beta</c>) used in the window title.</summary>
    public static class AppVersion
    {
        /// <summary>Friendly version string (<c>InformationalVersion</c>), e.g. <c>0.1.0 Beta</c>.</summary>
        public static string Display { get; } = Read();

        /// <summary>Numeric assembly version (e.g. <c>0.1.0.0</c>), used to compare against GitHub releases
        /// when checking for updates.</summary>
        public static Version Version { get; } =
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        private static string Read()
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info))
            {
                return "";
            }
            // Strip any "+<git commit>" build metadata the SDK may append.
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
    }
}
