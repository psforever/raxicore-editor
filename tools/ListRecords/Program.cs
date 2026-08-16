// Record inspector -- CLI front end.
//
// The actual logic lives in RaxicoreEditor.Generation.Records.ListRecordsTool, shared with the
// Editor's Generate menu so there is exactly one implementation.
//
// Usage:
//   dotnet run --project tools/ListRecords -- <PlanetSideDir> detail <RecordName>

using RaxicoreEditor.Generation;
using RaxicoreEditor.Generation.Records;

namespace RaxicoreEditor.Tools.ListRecords
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string ps = args.Length > 0 ? args[0] : "";
            string mode = args.Length > 1 ? args[1] : "";
            string target = args.Length > 2 ? args[2] : "";

            // "detail" is the only mode this has ever implemented; kept as an explicit switch (rather
            // than ignoring the argument) so a typo doesn't silently look like a clean "not found" run.
            if (mode != "detail")
            {
                Console.Error.WriteLine("usage: <PlanetSideDir> detail <RecordName>");
                return 1;
            }

            var options = new ListRecordsTool.Options(ps, target);
            var log = new SynchronousProgress<string>(Console.WriteLine);
            try
            {
                ListRecordsTool.Run(options, log);
                return 0;
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"usage: {e.Message}");
                return 1;
            }
        }
    }
}
