using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RaxicoreEditor.EngineAssets.Databases;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Structured <c>game_objects.adb</c> registry view. Master/detail: the object table (class_id /
    /// name / type / prop count, filterable) and, for the selected object, its property table
    /// (key → values). Read-only; Export returns the original bytes.
    /// </summary>
    public sealed class GameObjectDocument : DocumentBase
    {
        private readonly byte[] _data;
        private ObjRow? _selected;
        private string _filter = "";

        public GameObjectDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Database)
        {
            _data = data;
            try
            {
                GameObjectDb db = GameObjectDb.Parse(data);
                foreach (GameObjectDb.GameObject o in db.Objects)
                {
                    _all.Add(new ObjRow(o));
                }
                Info = $"game_objects registry · {db.Objects.Count} classes (name index {db.NameIndexCount})";
            }
            catch (Exception ex)
            {
                Info = "parse failed: " + ex.Message;
            }
            ApplyFilter();
            if (Objects.Count > 0)
            {
                SelectedObject = Objects[0];
            }
        }

        private readonly List<ObjRow> _all = new();

        public ObservableCollection<ObjRow> Objects { get; } = new();
        public ObservableCollection<PropRow> Properties { get; } = new();
        public string Info { get; } = "";

        public string Filter
        {
            get => _filter;
            set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
        }

        public ObjRow? SelectedObject
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value))
                {
                    RebuildProperties();
                }
            }
        }

        public override byte[] Export() => _data;

        private void ApplyFilter()
        {
            Objects.Clear();
            foreach (ObjRow r in _all)
            {
                if (string.IsNullOrEmpty(_filter) ||
                    r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                    r.Type.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    Objects.Add(r);
                }
            }
        }

        private void RebuildProperties()
        {
            Properties.Clear();
            if (_selected == null)
            {
                return;
            }
            foreach (KeyValuePair<string, List<string>> kv in _selected.Source.Properties)
            {
                Properties.Add(new PropRow(kv.Key, string.Join("  ", kv.Value)));
            }
        }

        public sealed class ObjRow
        {
            public ObjRow(GameObjectDb.GameObject o)
            {
                Source = o;
                ClassId = o.ClassId;
                Name = o.Name;
                Type = o.Type;
                PropCount = o.Properties.Count;
            }

            public GameObjectDb.GameObject Source { get; }
            public int ClassId { get; }
            public string Name { get; }
            public string Type { get; }
            public int PropCount { get; }
        }

        public sealed class PropRow
        {
            public PropRow(string key, string value) { Key = key; Value = value; }
            public string Key { get; }
            public string Value { get; }
        }
    }
}
