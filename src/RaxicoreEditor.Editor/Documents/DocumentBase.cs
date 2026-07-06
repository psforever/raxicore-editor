using System.Windows.Input;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.Documents
{
    public enum DocumentKind
    {
        Text,
        Hex,
        Image,
        Mesh,
        Surface,
        Animation,
        Database,
        Audio,
        Unknown,
    }

    /// <summary>
    /// A single open document shown as a tab in the center. Subtypes provide type-specific
    /// view/edit surfaces (selected by data template) and an export payload.
    /// </summary>
    public abstract class DocumentBase : ObservableObject
    {
        private bool _isDirty;
        private string _title = "untitled";

        protected DocumentBase(string title, string source, DocumentKind kind)
        {
            _title = title;
            Source = source;
            Kind = kind;
        }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>Origin description (disk path or "archive!entry").</summary>
        public string Source { get; }

        public DocumentKind Kind { get; }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (SetProperty(ref _isDirty, value))
                {
                    RaisePropertyChanged(nameof(TabHeader));
                }
            }
        }

        public string TabHeader => IsDirty ? Title + " *" : Title;

        /// <summary>Whether this document can produce an export payload.</summary>
        public virtual bool CanExport => true;

        /// <summary>Suggested filename when exporting.</summary>
        public virtual string SuggestedFileName => Title;

        /// <summary>Produce the bytes to write on export / re-pack.</summary>
        public abstract byte[] Export();

        /// <summary>Close command, wired by the shell.</summary>
        public ICommand? CloseCommand { get; set; }
    }
}
