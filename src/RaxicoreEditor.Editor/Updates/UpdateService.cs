using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RaxicoreEditor.Editor.Updates
{
    /// <summary>A newer release found on GitHub, with the Windows installer asset to download.</summary>
    public sealed record UpdateInfo(
        Version Version,
        string TagName,
        string DisplayName,
        string Notes,
        string DownloadUrl,
        string AssetName,
        long Size,
        string HtmlUrl);

    /// <summary>
    /// Checks the project's GitHub releases for a newer build and downloads the matching NSIS installer.
    /// Everything here is best-effort and side-effect-free until <see cref="DownloadAsync"/> /
    /// <see cref="LaunchInstaller"/> are called — those are only invoked from an explicit user action.
    /// </summary>
    public static class UpdateService
    {
        private const string Owner = "psforever";
        private const string Repo = "raxicore-editor";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub's API requires a User-Agent; the Accept header opts into the stable v3 JSON.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RaxicoreEditor-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>Query GitHub for the newest release that ships a Windows <c>.exe</c> installer and is
        /// strictly newer than the running build. Returns <c>null</c> if up to date, if nothing suitable is
        /// published, or on any network/parse error (callers treat null as "no update"). Prereleases are
        /// included, since the app itself ships as a beta.</summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20";
            using HttpResponseMessage resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            UpdateInfo? best = null;
            foreach (JsonElement rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                Version? version = ParseVersion(GetString(rel, "tag_name"));
                if (version is null)
                {
                    continue;
                }

                // Only releases carrying a Windows installer are actionable.
                if (!TryFindInstallerAsset(rel, out string assetUrl, out string assetName, out long size))
                {
                    continue;
                }

                if (best is null || version > best.Version)
                {
                    string tag = GetString(rel, "tag_name");
                    string name = GetString(rel, "name");
                    best = new UpdateInfo(
                        version,
                        tag,
                        string.IsNullOrWhiteSpace(name) ? tag : name,
                        GetString(rel, "body"),
                        assetUrl,
                        assetName,
                        size,
                        GetString(rel, "html_url"));
                }
            }

            return best is not null && best.Version > AppVersion.Version ? best : null;
        }

        /// <summary>Download the installer asset to a temp folder, reporting fractional progress (0..1).
        /// Returns the downloaded file's path.</summary>
        public static async Task<string> DownloadAsync(
            UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            string dir = Path.Combine(Path.GetTempPath(), "RaxicoreEditor", "updates");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, info.AssetName);

            using HttpResponseMessage resp =
                await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using Stream src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    progress?.Report((double)read / total);
                }
            }
            return dest;
        }

        /// <summary>Start the downloaded installer as a detached process. The per-user NSIS installer needs
        /// no elevation and relaunches the app from its finish page; the caller shuts the app down right
        /// after so the installer can overwrite the running files.</summary>
        public static void LaunchInstaller(string installerPath)
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }

        /// <summary>Parse a release tag (<c>v0.1.1</c>, <c>0.1.1</c>, <c>v0.2.0-beta</c>, …) into a 4-part
        /// <see cref="Version"/> for comparison, dropping any leading <c>v</c> and pre-release/build suffix.
        /// Returns null if it can't be parsed.</summary>
        public static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(1);
            }
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0)
            {
                s = s.Substring(0, cut);
            }
            if (!Version.TryParse(s, out Version? v))
            {
                return null;
            }
            // Normalise unset components to 0 so 0.1 and 0.1.0.0 compare equal.
            return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        private static bool TryFindInstallerAsset(JsonElement release, out string url, out string name, out long size)
        {
            url = "";
            name = "";
            size = 0;
            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string n = GetString(a, "name");
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = GetString(a, "browser_download_url");
                    name = n;
                    size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long v) ? v : 0;
                    return !string.IsNullOrEmpty(url);
                }
            }
            return false;
        }

        private static string GetString(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
    }
}
