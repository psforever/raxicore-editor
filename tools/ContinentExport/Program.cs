// Continent terrain exporter -- CLI front end.
//
// The actual logic lives in RaxicoreEditor.Generation.Continents.ContinentExportTool, shared with the
// Editor's Generate menu so there is exactly one implementation.
//
// Usage:
//   dotnet run --project tools/ContinentExport -- --planetside <PlanetSideDir> --out <OutDir> [--terrain-out <Dir>]

using RaxicoreEditor.Generation;
using RaxicoreEditor.Generation.Continents;

namespace RaxicoreEditor.Tools.ContinentExport
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string? planetside = Arg(args, "--planetside");
            string? outDir = Arg(args, "--out");
            string? terrainOutDir = Arg(args, "--terrain-out");

            if (string.IsNullOrEmpty(planetside) || string.IsNullOrEmpty(outDir))
            {
                Console.Error.WriteLine("usage: --planetside <dir> --out <dir> [--terrain-out <dir>]");
                return 1;
            }

            var options = new ContinentExportTool.Options(planetside, outDir, terrainOutDir);
            var log = new SynchronousProgress<string>(Console.WriteLine);
            try
            {
                ContinentExportTool.Run(options, log);
                return 0;
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"usage: {e.Message}");
                return 1;
            }
        }

        private static string? Arg(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
    }
}
