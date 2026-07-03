using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RaxicoreEditor.EngineAssets.Databases;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Structured ADB (chunky/asciidatabase) preview. Shows the section keyword + symbol/token counts
    /// as a header and the NUL-delimited token stream as an indexed table. Read-only; Export returns
    /// the original bytes.
    /// </summary>
    public sealed class AdbDocument : DocumentBase
    {
        private readonly byte[] _data;

        public AdbDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Database)
        {
            _data = data;
            try
            {
                AdbFile adb = AdbFile.Parse(data);
                for (int i = 0; i < adb.Tokens.Count; i++)
                {
                    _allTokens.Add(new TokenRow(i, adb.Tokens[i]));
                }
                Info = $"section '{adb.SectionKeyword}' · {adb.Tokens.Count} strings (pool {adb.SymbolCount} B)" +
                       (adb.CommandStreamLength > 0 ? $" · {adb.CommandStreamLength} B binary records (not shown)" : "");
            }
            catch (Exception ex)
            {
                Info = "parse failed: " + ex.Message;
            }
            ApplyFilter();
        }

        private readonly List<TokenRow> _allTokens = new();
        private string _filter = "";

        public ObservableCollection<TokenRow> Tokens { get; } = new();
        public string Info { get; } = "";

        /// <summary>Substring filter over the token values.</summary>
        public string Filter
        {
            get => _filter;
            set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
        }

        private void ApplyFilter()
        {
            Tokens.Clear();
            foreach (TokenRow row in _allTokens)
            {
                if (string.IsNullOrEmpty(_filter) ||
                    row.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    Tokens.Add(row);
                }
            }
        }

        public override byte[] Export() => _data;

        public sealed class TokenRow
        {
            public TokenRow(int index, string value)
            {
                Index = index;
                Value = value;
            }

            public int Index { get; }
            public string Value { get; }
        }
    }
}
