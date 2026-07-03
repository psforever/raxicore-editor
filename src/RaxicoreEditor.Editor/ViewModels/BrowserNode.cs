using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.ViewModels
{
    public enum BrowserNodeKind
    {
        Folder,
        File,
        Archive,
        ArchiveEntry,
        Category,
    }

    /// <summary>A node in the asset browser tree (category / folder / file / opened archive / entry).</summary>
    public sealed class BrowserNode : ObservableObject
    {
        private bool _isExpanded;
        private Action<BrowserNode>? _lazyLoader;
        private bool _lazyLoaded;

        public BrowserNode(string name, string fullPath, BrowserNodeKind kind)
        {
            _name = name;
            FullPath = fullPath;
            Kind = kind;
        }

        private string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>Disk path, or "archiveLabel!entryName" for archive entries.</summary>
        public string FullPath { get; }

        public BrowserNodeKind Kind { get; }

        public ObservableCollection<BrowserNode> Children { get; } = new();

        /// <summary>
        /// Defer populating children until first expand (used by the game-asset catalog so opening 169
        /// texture archives or scanning content PACKs only costs work when the user actually looks).
        /// </summary>
        public void SetLazyLoader(Action<BrowserNode> loader)
        {
            _lazyLoader = loader;
            _lazyLoaded = false;
            // A placeholder gives the node an expander arrow before its real children exist.
            Children.Add(new BrowserNode("…", FullPath + "#pending", BrowserNodeKind.File));
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value) && value && _lazyLoader != null && !_lazyLoaded)
                {
                    _lazyLoaded = true;
                    Children.Clear();
                    try { _lazyLoader(this); }
                    catch { /* loader logs its own failure */ }
                }
            }
        }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public bool IsLeaf => Kind is BrowserNodeKind.File or BrowserNodeKind.ArchiveEntry;

        // File-type icon (vector geometry + colour), selected by node kind + extension.
        private (Geometry Geometry, IBrush Brush)? _icon;
        private (Geometry Geometry, IBrush Brush) IconInfo => _icon ??=
            FileIcons.For(Kind == BrowserNodeKind.Category ? BrowserNodeKind.Folder : Kind, Name);

        public Geometry Icon => IconInfo.Geometry;
        public IBrush IconBrush => IconInfo.Brush;
    }
}
