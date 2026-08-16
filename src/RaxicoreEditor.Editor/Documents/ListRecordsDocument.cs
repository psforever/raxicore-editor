using System;
using System.IO;
using System.Threading;
using RaxicoreEditor.Generation.Records;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Generate tab for <see cref="ListRecordsTool"/>: per-mesh vertex/material stats for one named
    /// record, searched across every mesh library.
    /// </summary>
    public sealed class ListRecordsDocument : GenerationDocumentBase
    {
        private string _planetSideDir = "";
        private string _recordName = "";

        public ListRecordsDocument(string suggestedPlanetSideDir)
            : base("Record Detail Report")
        {
            _planetSideDir = suggestedPlanetSideDir;
        }

        public string PlanetSideDir
        {
            get => _planetSideDir;
            set
            {
                if (SetProperty(ref _planetSideDir, value))
                {
                    NotifyOptionsChanged();
                }
            }
        }

        /// <summary>The mesh system's record name to look up, e.g. <c>warpgate</c>.</summary>
        public string RecordName
        {
            get => _recordName;
            set
            {
                if (SetProperty(ref _recordName, value))
                {
                    NotifyOptionsChanged();
                }
            }
        }

        public override string? ValidationError
        {
            get
            {
                if (string.IsNullOrWhiteSpace(PlanetSideDir))
                {
                    return "Choose the PlanetSide reference client folder.";
                }
                if (!Directory.Exists(PlanetSideDir))
                {
                    return "That PlanetSide folder does not exist.";
                }
                if (string.IsNullOrWhiteSpace(RecordName))
                {
                    return "Enter a record name to look up.";
                }
                return null;
            }
        }

        protected override void Execute(IProgress<string> log, CancellationToken ct)
        {
            var options = new ListRecordsTool.Options(PlanetSideDir, RecordName);
            int found = ListRecordsTool.Run(options, log, ct);
            if (found > 0)
            {
                log.Report($"Found in {found} librar{(found == 1 ? "y" : "ies")}.");
            }
        }
    }
}
