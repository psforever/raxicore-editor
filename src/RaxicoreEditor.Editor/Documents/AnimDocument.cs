using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Structured ANIM (anims.ubr) preview. Master/detail tables: an animation list (name, duration,
    /// track count) and, for the selected animation, its per-track table (flags + spline-key counts).
    /// Read-only; Export returns the original bytes.
    /// </summary>
    public sealed class AnimDocument : DocumentBase
    {
        private readonly byte[] _data;
        private AnimRow? _selectedAnimation;

        public AnimDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Animation)
        {
            _data = data;
            try
            {
                AnimDb db = AnimDb.Load(data);
                foreach (AnimRecord rec in db.Records)
                {
                    _allAnimations.Add(new AnimRow(rec));
                }
                Info = $"ANIM v{db.Version} · {db.RecordCount} animations";
                ApplyFilter();
                if (Animations.Count > 0)
                {
                    SelectedAnimation = Animations[0];
                }
            }
            catch (Exception ex)
            {
                Info = "parse failed: " + ex.Message;
            }
        }

        private readonly List<AnimRow> _allAnimations = new();
        private string _filter = "";

        public ObservableCollection<AnimRow> Animations { get; } = new();
        public ObservableCollection<TrackRow> Tracks { get; } = new();
        public string Info { get; } = "";

        /// <summary>Substring filter over the animation names.</summary>
        public string Filter
        {
            get => _filter;
            set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
        }

        private void ApplyFilter()
        {
            Animations.Clear();
            foreach (AnimRow row in _allAnimations)
            {
                if (string.IsNullOrEmpty(_filter) ||
                    row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    Animations.Add(row);
                }
            }
        }

        public AnimRow? SelectedAnimation
        {
            get => _selectedAnimation;
            set
            {
                if (SetProperty(ref _selectedAnimation, value))
                {
                    RebuildTracks();
                }
            }
        }

        public override byte[] Export() => _data;

        private void RebuildTracks()
        {
            Tracks.Clear();
            if (_selectedAnimation == null)
            {
                return;
            }
            foreach (AnimTrack t in _selectedAnimation.Source.Tracks)
            {
                Tracks.Add(new TrackRow(t));
            }
        }

        public sealed class AnimRow
        {
            public AnimRow(AnimRecord rec)
            {
                Source = rec;
                Name = rec.Name;
                Duration = rec.Duration;
                TrackCount = rec.Tracks.Count;
            }

            public AnimRecord Source { get; }
            public string Name { get; }
            public float Duration { get; }
            public int TrackCount { get; }
        }

        public sealed class TrackRow
        {
            public TrackRow(AnimTrack t)
            {
                Name = t.Name;
                PosFlags = "0x" + t.PosFlags.ToString("X");
                PosKeys = t.PosSplineCount;
                RotFlags = "0x" + t.RotFlags.ToString("X");
                RotKeys = t.RotSplineCount;
                Animated = (t.HasPosSpline ? "pos" : "") + (t.HasRotSpline ? (t.HasPosSpline ? "+rot" : "rot") : "");
            }

            public string Name { get; }
            public string PosFlags { get; }
            public uint PosKeys { get; }
            public string RotFlags { get; }
            public uint RotKeys { get; }
            public string Animated { get; }
        }
    }
}
