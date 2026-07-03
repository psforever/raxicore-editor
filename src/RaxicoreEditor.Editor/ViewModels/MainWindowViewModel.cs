using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Databases;
using RaxicoreEditor.EngineAssets.Meshes;
using RaxicoreEditor.EngineAssets.Surfaces;
using RaxicoreEditor.EngineAssets.Textures;
using RaxicoreEditor.Editor.Documents;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.ViewModels
{
    public sealed class MainWindowViewModel : ObservableObject
    {
        private const int MaxFolderFiles = 20000;

        private DocumentBase? _selectedDocument;
        private BrowserNode? _selectedNode;

        public MainWindowViewModel()
        {
            CloseDocumentCommand = new RelayCommand<DocumentBase>(CloseDocument);
            ShowGameAssetsCommand = new RelayCommand(() => GameAssetsView = true);
            ShowFilesCommand = new RelayCommand(() => GameAssetsView = false);
            Log("Raxicore Editor ready. Open a folder or archive to begin.");
        }

        /// <summary>Game-utilization view: assets grouped by role (default left-pane view).</summary>
        public ObservableCollection<BrowserNode> CatalogRoots { get; } = new();
        /// <summary>Raw on-disk folder tree (secondary left-pane view).</summary>
        public ObservableCollection<BrowserNode> Roots { get; } = new();
        public ObservableCollection<DocumentBase> OpenDocuments { get; } = new();
        public ObservableCollection<string> ConsoleLines { get; } = new();

        public RelayCommand ShowGameAssetsCommand { get; }
        public RelayCommand ShowFilesCommand { get; }

        private bool _gameAssetsView = true;
        /// <summary>True = show the game-asset catalog; false = show the raw folder tree.</summary>
        public bool GameAssetsView
        {
            get => _gameAssetsView;
            set
            {
                if (SetProperty(ref _gameAssetsView, value))
                {
                    RaisePropertyChanged(nameof(FilesView));
                }
            }
        }

        public bool FilesView => !_gameAssetsView;

        private readonly Dictionary<string, PakArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FlatArchive> _flats = new(StringComparer.OrdinalIgnoreCase);

        public RelayCommand<DocumentBase> CloseDocumentCommand { get; }

        public DocumentBase? SelectedDocument
        {
            get => _selectedDocument;
            set => SetProperty(ref _selectedDocument, value);
        }

        public BrowserNode? SelectedNode
        {
            get => _selectedNode;
            set => SetProperty(ref _selectedNode, value);
        }

        private string _browserFilter = "";
        /// <summary>Filters the asset tree by name (a node stays visible if it or any descendant matches).</summary>
        public string BrowserFilter
        {
            get => _browserFilter;
            set
            {
                if (SetProperty(ref _browserFilter, value))
                {
                    foreach (BrowserNode root in Roots)
                    {
                        ApplyFilter(root, value);
                    }
                    foreach (BrowserNode root in CatalogRoots)
                    {
                        ApplyFilter(root, value);
                    }
                }
            }
        }

        private static bool ApplyFilter(BrowserNode node, string filter)
        {
            bool selfMatch = string.IsNullOrEmpty(filter) ||
                             node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            bool childVisible = false;
            foreach (BrowserNode child in node.Children)
            {
                childVisible |= ApplyFilter(child, filter);
            }
            node.IsVisible = selfMatch || childVisible;
            if (!string.IsNullOrEmpty(filter) && childVisible)
            {
                node.IsExpanded = true;
            }
            return node.IsVisible;
        }

        // ---- folder mounting -------------------------------------------------------------------

        public void MountFolder(string path)
        {
            try
            {
                var root = new BrowserNode(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                                           path, BrowserNodeKind.Folder) { IsExpanded = true };
                int count = PopulateFolder(root, path, 0);
                Roots.Add(root);
                Log($"Mounted '{path}' ({count} files).");
                BuildCatalog(path);
            }
            catch (Exception ex)
            {
                Log($"mount failed: {ex.Message}");
            }
        }

        private int PopulateFolder(BrowserNode node, string dir, int depth)
        {
            int count = 0;
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                return 0;
            }

            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string sub in subdirs)
            {
                if (count >= MaxFolderFiles)
                {
                    break;
                }
                var child = new BrowserNode(Path.GetFileName(sub), sub, BrowserNodeKind.Folder);
                count += PopulateFolder(child, sub, depth + 1);
                node.Children.Add(child);
            }

            foreach (string file in files)
            {
                if (count >= MaxFolderFiles)
                {
                    break;
                }
                node.Children.Add(new BrowserNode(Path.GetFileName(file), file, BrowserNodeKind.File));
                count++;
            }
            return count;
        }

        // ---- game-asset catalog ----------------------------------------------------------------

        private static readonly string[] SkipDirs = { "launchpad.libs", "redist", "wiredred" };

        /// <summary>
        /// Build the game-utilization view: every asset under <paramref name="root"/> grouped by the role
        /// it plays in the game (continents, models, textures, ASCII databases, …) rather than by folder.
        /// Archive contents (textures, PACK databases) load lazily on expand so the scan stays instant.
        /// </summary>
        public void BuildCatalog(string root)
        {
            var continents = new BrowserNode("Continents", root + "#continents", BrowserNodeKind.Category);
            var models = new BrowserNode("Models", root + "#models", BrowserNodeKind.Category);
            var anims = new BrowserNode("Animations", root + "#anims", BrowserNodeKind.Category);
            var textures = new BrowserNode("Textures", root + "#tex", BrowserNodeKind.Category);
            var surfaces = new BrowserNode("Surfaces", root + "#surf", BrowserNodeKind.Category);
            var databases = new BrowserNode("Databases (ASCII)", root + "#adb", BrowserNodeKind.Category);
            var audio = new BrowserNode("Audio", root + "#audio", BrowserNodeKind.Category);
            var scripts = new BrowserNode("Scripts & Config", root + "#cfg", BrowserNodeKind.Category);
            var archives = new BrowserNode("Archives", root + "#pak", BrowserNodeKind.Category);
            var other = new BrowserNode("Other", root + "#other", BrowserNodeKind.Category);

            var files = new List<string>();
            CollectFiles(root, files, 0);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                string ext = Path.GetExtension(name).ToLowerInvariant();
                switch (ext)
                {
                    case ".ubr":
                        if (IsContinentUbr(name)) continents.Children.Add(Leaf(name, f));
                        else if (name.Contains("anim", StringComparison.OrdinalIgnoreCase)) anims.Children.Add(Leaf(name, f));
                        else models.Children.Add(Leaf(name, f));
                        break;
                    case ".fat":
                        textures.Children.Add(LazyArchive(name, f, n => LoadFlatEntries(n, f)));
                        break;
                    case ".fdx":
                        break; // index companion to a .fat, surfaced via the .fat node
                    case ".dds":
                        textures.Children.Add(Leaf(name, f));
                        break;
                    case ".srf":
                        surfaces.Children.Add(Leaf(name, f));
                        break;
                    case ".adb":
                        databases.Children.Add(Leaf(name, f));
                        break;
                    case ".pak":
                        archives.Children.Add(LazyArchive(name, f, n => LoadPakEntries(n, f, databasesOnly: false)));
                        if (name.EndsWith("_srf.pak", StringComparison.OrdinalIgnoreCase))
                            surfaces.Children.Add(LazyArchive(name, f, n => LoadPakEntries(n, f, databasesOnly: false)));
                        else if (IsContentPak(f, name))
                            databases.Children.Add(LazyArchive(name + "  →  ASCII databases", f, n => LoadPakEntries(n, f, databasesOnly: true)));
                        if (name.Contains("audio", StringComparison.OrdinalIgnoreCase))
                            audio.Children.Add(LazyArchive(name, f, n => LoadPakEntries(n, f, databasesOnly: false)));
                        break;
                    case ".wav":
                    case ".bik":
                        audio.Children.Add(Leaf(name, f));
                        break;
                    case ".ini":
                    case ".lst":
                    case ".cfg":
                    case ".txt":
                    case ".log":
                        scripts.Children.Add(Leaf(name, f));
                        break;
                    case ".dll":
                    case ".exe":
                    case ".ico":
                    case ".ocx":
                    case ".soe":
                    case ".orig":
                    case ".swp":
                    case ".name":
                        break; // system / non-asset files
                    default:
                        other.Children.Add(Leaf(name, f));
                        break;
                }
            }

            CatalogRoots.Clear();
            BrowserNode[] cats = { continents, models, anims, textures, surfaces, databases, audio, scripts, archives, other };
            int shown = 0;
            foreach (BrowserNode cat in cats)
            {
                if (cat.Children.Count == 0) continue;
                cat.Name = $"{cat.Name}  ({cat.Children.Count})";
                cat.IsExpanded = false;
                CatalogRoots.Add(cat);
                shown++;
            }
            Log($"Game-asset catalog: {shown} categories built. (Default view; switch to Files at the bottom of the pane.)");
        }

        private void CollectFiles(string dir, List<string> outFiles, int depth)
        {
            if (outFiles.Count >= MaxFolderFiles) return;
            string[] subdirs, files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { return; }

            foreach (string file in files)
            {
                if (outFiles.Count >= MaxFolderFiles) return;
                outFiles.Add(file);
            }
            foreach (string sub in subdirs)
            {
                string leaf = Path.GetFileName(sub).ToLowerInvariant();
                if (Array.IndexOf(SkipDirs, leaf) >= 0) continue;
                CollectFiles(sub, outFiles, depth + 1);
            }
        }

        private static BrowserNode Leaf(string name, string path) => new(name, path, BrowserNodeKind.File);

        private static BrowserNode LazyArchive(string name, string path, Action<BrowserNode> loader)
        {
            var node = new BrowserNode(name, path, BrowserNodeKind.Archive);
            node.SetLazyLoader(loader);
            return node;
        }

        /// <summary>map01.ubr … map99.ubr are the playable continents (terrain meshes).</summary>
        private static bool IsContinentUbr(string name)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (!baseName.StartsWith("map", StringComparison.OrdinalIgnoreCase) || baseName.Length <= 3) return false;
            for (int i = 3; i < baseName.Length; i++)
            {
                if (!char.IsDigit(baseName[i])) return false;
            }
            return true;
        }

        /// <summary>Content PACKs that hold chunky/ASCII databases (vs surface/resource/audio PACKs).</summary>
        private static bool IsContentPak(string fullPath, string name)
        {
            if (name.Equals("startup.pak", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("expansion1.pak", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string norm = fullPath.Replace('\\', '/');
            return norm.Contains("/pack/", StringComparison.OrdinalIgnoreCase);
        }

        private void LoadFlatEntries(BrowserNode node, string fatPath)
        {
            try
            {
                if (!_flats.TryGetValue(fatPath, out FlatArchive? flat))
                {
                    flat = FlatArchive.Load(File.ReadAllBytes(fatPath));
                    _flats[fatPath] = flat;
                }
                foreach (FlatEntry e in flat.Entries)
                {
                    node.Children.Add(new BrowserNode(e.Name, fatPath + "!" + e.Name, BrowserNodeKind.ArchiveEntry));
                }
            }
            catch (Exception ex)
            {
                Log($"index '{Path.GetFileName(fatPath)}' failed: {ex.Message}");
            }
        }

        private void LoadPakEntries(BrowserNode node, string pakPath, bool databasesOnly)
        {
            try
            {
                if (!_archives.TryGetValue(pakPath, out PakArchive? pak))
                {
                    pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                    _archives[pakPath] = pak;
                }
                int added = 0;
                foreach (PakEntry e in pak.Entries)
                {
                    if (databasesOnly && !IsDatabaseEntry(pak, e))
                    {
                        continue;
                    }
                    node.Children.Add(new BrowserNode(e.Name, pakPath + "!" + e.Name, BrowserNodeKind.ArchiveEntry));
                    added++;
                }
                if (databasesOnly && added == 0)
                {
                    node.Children.Add(new BrowserNode("(no ASCII databases found)", pakPath + "#none", BrowserNodeKind.File));
                }
            }
            catch (Exception ex)
            {
                Log($"open '{Path.GetFileName(pakPath)}' failed: {ex.Message}");
            }
        }

        private static readonly string[] TextDbExts = { ".adb", ".txt", ".ui", ".inc", ".def", ".lst", ".cfg", ".dat", ".csv", ".ini" };
        private static readonly string[] KnownBinaryExts = { ".wav", ".dds", ".bik", ".x", ".ogg", ".mp3", ".tga", ".bmp", ".scc", ".fnt", ".ttf", ".cg" };

        // An "ASCII" entry is a chunky/asciidatabase blob OR a plain-text file (e.g. ui.pak's .ui/.txt/.inc).
        // Cheap extension checks first; otherwise extract once and test the magic / printable-ASCII ratio.
        private static bool IsDatabaseEntry(PakArchive pak, PakEntry e)
        {
            string ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (Array.IndexOf(TextDbExts, ext) >= 0) return true;
            if (Array.IndexOf(KnownBinaryExts, ext) >= 0) return false;
            try
            {
                byte[] b = pak.Extract(e.Name);
                return AdbFile.IsChunky(b) || LooksAscii(b);
            }
            catch { return false; }
        }

        private static bool LooksAscii(byte[] b)
        {
            if (b.Length == 0) return false;
            int n = Math.Min(b.Length, 4096), printable = 0;
            for (int i = 0; i < n; i++)
            {
                byte c = b[i];
                if (c == 0) return false; // a NUL byte means binary
                if (c == 9 || c == 10 || c == 13 || (c >= 0x20 && c <= 0x7E)) printable++;
            }
            return printable >= (int)(n * 0.95);
        }

        // ---- opening documents -----------------------------------------------------------------

        public void OpenNode(BrowserNode node)
        {
            switch (node.Kind)
            {
                case BrowserNodeKind.File:
                    OpenPath(node.FullPath);
                    break;
                case BrowserNodeKind.ArchiveEntry:
                    OpenArchiveEntry(node);
                    break;
            }
        }

        /// <summary>Open a folder (mount), a PACK archive (browse), or a plain file (document).</summary>
        public void OpenPath(string path)
        {
            if (Directory.Exists(path))
            {
                MountFolder(path);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Log($"open '{path}' failed: {ex.Message}");
                return;
            }

            if (PakArchive.HasMagic(bytes))
            {
                LoadPak(path, bytes);
                return;
            }
            if (FlatArchive.HasMagic(bytes))
            {
                LoadFlat(path, bytes);
                return;
            }
            // .fdx is the FLAT index companion: open it as access to its .fat archive.
            if (path.EndsWith(".fdx", StringComparison.OrdinalIgnoreCase))
            {
                OpenFdx(path);
                return;
            }

            OpenDocumentFromBytes(Path.GetFileName(path), path, bytes);
        }

        /// <summary>Open a .fdx index by browsing its sibling .fat archive (the data store).</summary>
        private void OpenFdx(string fdxPath)
        {
            string fatPath = Path.ChangeExtension(fdxPath, ".fat");
            if (File.Exists(fatPath))
            {
                try
                {
                    LoadFlat(fatPath, File.ReadAllBytes(fatPath));
                    Log($"Opened '{Path.GetFileName(fdxPath)}' (.fdx index) → browsing {Path.GetFileName(fatPath)}.");
                }
                catch (Exception ex)
                {
                    Log($"open .fdx '{fdxPath}' failed: {ex.Message}");
                }
                return;
            }
            Log($".fdx '{Path.GetFileName(fdxPath)}' has no sibling .fat data file beside it.");
        }

        private void LoadFlat(string path, byte[] bytes)
        {
            FlatArchive flat;
            try
            {
                flat = FlatArchive.Load(bytes);
            }
            catch (Exception ex)
            {
                Log($"parse FLAT '{path}' failed: {ex.Message}");
                return;
            }

            _flats[path] = flat;
            var root = new BrowserNode(Path.GetFileName(path), path, BrowserNodeKind.Archive)
            {
                IsExpanded = true,
            };
            foreach (FlatEntry e in flat.Entries)
            {
                root.Children.Add(new BrowserNode(e.Name, path + "!" + e.Name, BrowserNodeKind.ArchiveEntry));
            }
            Roots.Add(root);
            Log($"Opened archive {Path.GetFileName(path)} (FLAT, {flat.Entries.Count} entries).");
        }

        private void LoadPak(string path, byte[] bytes)
        {
            PakArchive pak;
            try
            {
                pak = PakArchive.Load(bytes);
            }
            catch (Exception ex)
            {
                Log($"parse PACK '{path}' failed: {ex.Message}");
                return;
            }

            _archives[path] = pak;
            var root = new BrowserNode(Path.GetFileName(path), path, BrowserNodeKind.Archive)
            {
                IsExpanded = true,
            };
            foreach (PakEntry e in pak.Entries)
            {
                root.Children.Add(new BrowserNode(e.Name, path + "!" + e.Name, BrowserNodeKind.ArchiveEntry));
            }
            Roots.Add(root);
            Log($"Opened archive {Path.GetFileName(path)} (PACK v{pak.Version}, {pak.Entries.Count} entries).");
        }

        private void OpenArchiveEntry(BrowserNode node)
        {
            int bang = node.FullPath.IndexOf('!');
            if (bang < 0)
            {
                return;
            }
            string archivePath = node.FullPath.Substring(0, bang);
            string entryName = node.FullPath.Substring(bang + 1);

            byte[] bytes;
            try
            {
                if (_archives.TryGetValue(archivePath, out PakArchive? pak))
                {
                    bytes = pak.Extract(entryName);
                }
                else if (_flats.TryGetValue(archivePath, out FlatArchive? flat))
                {
                    bytes = flat.Extract(entryName);
                }
                else
                {
                    Log("archive not loaded: " + archivePath);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"extract '{entryName}' failed: {ex.Message}");
                return;
            }
            OpenDocumentFromBytes(entryName, node.FullPath, bytes);
        }

        private void OpenDocumentFromBytes(string name, string source, byte[] bytes)
        {
            // Focus an already-open document for the same source.
            foreach (DocumentBase existing in OpenDocuments)
            {
                if (string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedDocument = existing;
                    return;
                }
            }

            DocumentBase doc = CreateDocument(name, source, bytes);
            AddDocument(doc);
            Log($"Opened {name} ({bytes.Length} bytes, {doc.Kind}).");
        }

        public void AddDocument(DocumentBase doc)
        {
            doc.CloseCommand = new RelayCommand(() => CloseDocument(doc));
            OpenDocuments.Add(doc);
            SelectedDocument = doc;
        }

        private static DocumentBase CreateDocument(string name, string path, byte[] bytes)
        {
            // Detect by magic first (archive entries carry meaningful payloads, not just extensions).
            if (DdsImage.HasMagic(bytes))
            {
                return new ImageDocument(name, path, bytes);
            }
            if (UberMesh.IsUberMesh(bytes))
            {
                return new MeshDocument(name, path, bytes);
            }
            if (AnimDb.IsAnim(bytes))
            {
                try { return new AnimDocument(name, path, bytes); }
                catch { return new HexDocument(name, path, bytes); }
            }
            if (AdbFile.IsChunky(bytes))
            {
                if (GameObjectDb.IsGameObjects(bytes))
                {
                    try { return new GameObjectDocument(name, path, bytes); }
                    catch { /* fall through to the generic ADB view */ }
                }
                try { return new AdbDocument(name, path, bytes); }
                catch { return new HexDocument(name, path, bytes); }
            }

            string ext = Path.GetExtension(name).ToLowerInvariant();
            switch (ext)
            {
                case ".srf":
                    try { return new SurfaceDocument(name, path, bytes); }
                    catch { return new HexDocument(name, path, bytes); }
                case ".txt":
                case ".lst":
                case ".ini":
                case ".cfg":
                case ".log":
                    return new TextDocument(name, path, DecodeText(bytes));
                default:
                    return new HexDocument(name, path, bytes);
            }
        }

        private static string DecodeText(byte[] bytes)
        {
            // Honour a BOM (e.g. news_unicode*.txt are UTF-16); otherwise engine-derived text is Latin1/ASCII.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);          // UTF-16 LE
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); // UTF-16 BE
            }
            // BOM-less UTF-16 LE heuristic: many high-plane bytes are 0x00 in the odd positions.
            if (bytes.Length >= 4 && bytes.Length % 2 == 0)
            {
                int zerosOdd = 0, sample = Math.Min(bytes.Length, 512);
                for (int i = 1; i < sample; i += 2) if (bytes[i] == 0) zerosOdd++;
                if (zerosOdd > sample / 4) return Encoding.Unicode.GetString(bytes);
            }
            return Encoding.Latin1.GetString(bytes);
        }

        public void CloseDocument(DocumentBase? doc)
        {
            if (doc is null)
            {
                return;
            }
            int index = OpenDocuments.IndexOf(doc);
            if (index < 0)
            {
                return;
            }
            OpenDocuments.RemoveAt(index);
            if (ReferenceEquals(SelectedDocument, doc))
            {
                SelectedDocument = OpenDocuments.Count > 0
                    ? OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)]
                    : null;
            }
        }

        // ---- console ---------------------------------------------------------------------------

        public void Log(string message)
        {
            ConsoleLines.Add(message);
            if (ConsoleLines.Count > 1000)
            {
                ConsoleLines.RemoveAt(0);
            }
        }
    }
}
