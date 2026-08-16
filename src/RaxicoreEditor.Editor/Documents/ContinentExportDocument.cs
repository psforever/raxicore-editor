using System;
using System.IO;
using System.Threading;
using RaxicoreEditor.Generation.Continents;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Generate tab for <see cref="ContinentExportTool"/>: per-continent terrain/facility JSON, plus an
    /// optional separate per-continent ground-height resource. The output makes no assumption about who
    /// consumes it -- any project that wants continent data can read it.
    /// </summary>
    public sealed class ContinentExportDocument : GenerationDocumentBase
    {
        private string _planetSideDir = "";
        private string _outputDir = "";
        private bool _exportTerrainHeights;
        private string _terrainOutputDir = "";

        public ContinentExportDocument(string suggestedPlanetSideDir)
            : base("Continent Export")
        {
            _planetSideDir = suggestedPlanetSideDir;
        }

        /// <summary>The read-only reference client folder (contains <c>uber.ubr</c>, <c>map01.ubr</c>, ...).</summary>
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

        /// <summary>Where the per-continent JSON and <c>footprints.json</c> are written.</summary>
        public string OutputDir
        {
            get => _outputDir;
            set
            {
                if (SetProperty(ref _outputDir, value))
                {
                    NotifyOptionsChanged();
                }
            }
        }

        /// <summary>
        /// Also write a per-continent ground-height resource to its own folder, separate from
        /// <see cref="OutputDir"/> -- for anything that wants real per-cell height directly.
        /// </summary>
        public bool ExportTerrainHeights
        {
            get => _exportTerrainHeights;
            set
            {
                if (SetProperty(ref _exportTerrainHeights, value))
                {
                    NotifyOptionsChanged();
                }
            }
        }

        public string TerrainOutputDir
        {
            get => _terrainOutputDir;
            set
            {
                if (SetProperty(ref _terrainOutputDir, value))
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
                if (string.IsNullOrWhiteSpace(OutputDir))
                {
                    return "Choose an output folder.";
                }
                if (ExportTerrainHeights && string.IsNullOrWhiteSpace(TerrainOutputDir))
                {
                    return "Choose a folder for the terrain height resources, or turn that option off.";
                }
                return null;
            }
        }

        protected override void Execute(IProgress<string> log, CancellationToken ct)
        {
            var options = new ContinentExportTool.Options(
                PlanetSideDir,
                OutputDir,
                ExportTerrainHeights ? TerrainOutputDir : null);
            ContinentExportTool.Result result = ContinentExportTool.Run(options, log, ct);
            log.Report(
                $"{result.ContinentsExported} continents, {result.FacilityFootprintTypes} facility types.");
        }
    }
}
