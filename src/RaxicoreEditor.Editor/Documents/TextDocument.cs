using System.Collections.ObjectModel;
using System.Text;
using RaxicoreEditor.Editor.Validation;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Editable text document (config .ini, keymap/command .lst/.cfg, plain .txt/.log). Parses and
    /// validates the content against its file type on every edit (see <see cref="Validation.TextValidator"/>),
    /// surfacing issues + a validity flag the shell uses to enforce format on export.
    /// </summary>
    public sealed class TextDocument : DocumentBase
    {
        private string _text;

        public TextDocument(string title, string source, string text)
            : base(title, source, DocumentKind.Text)
        {
            _text = text;
            Revalidate();
        }

        public string Text
        {
            get => _text;
            set
            {
                if (SetProperty(ref _text, value))
                {
                    IsDirty = true;
                    Revalidate();
                }
            }
        }

        public ObservableCollection<TextIssue> Issues { get; } = new();
        public string ValidationSummary { get; private set; } = "";
        public bool IsContentValid { get; private set; } = true;
        public bool HasIssues => Issues.Count > 0;

        private void Revalidate()
        {
            ValidationReport report = TextValidator.Validate(Title, _text);
            Issues.Clear();
            foreach (TextIssue i in report.Issues)
            {
                Issues.Add(i);
            }
            IsContentValid = report.IsValid;
            ValidationSummary = report.Summary;
            RaisePropertyChanged(nameof(ValidationSummary));
            RaisePropertyChanged(nameof(IsContentValid));
            RaisePropertyChanged(nameof(HasIssues));
        }

        public override byte[] Export() => Encoding.UTF8.GetBytes(_text);
    }
}
