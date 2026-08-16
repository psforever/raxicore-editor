using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// A tab that runs one of the "Generate" menu's export/report tools and shows its progress live.
    ///
    /// Unlike every other <see cref="DocumentBase"/>, this is not a viewer or editor for a file that
    /// already exists: it starts on an options form, and clicking Run drives a background task whose
    /// output streams into <see cref="Log"/> as it happens. There is nothing to export from the tab
    /// itself -- the tool writes its own output files directly -- so <see cref="CanExport"/> is false.
    /// </summary>
    public abstract class GenerationDocumentBase : DocumentBase
    {
        private bool _isRunning;
        private bool _hasRun;
        private bool _completedOk;
        private string _statusMessage = "Fill in the options below and click Run.";
        private CancellationTokenSource? _cts;

        protected GenerationDocumentBase(string title)
            : base(title, source: "generate:" + title, DocumentKind.Generation)
        {
            RunCommand = new RelayCommand(RunFireAndForget, () => CanRun);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        }

        /// <summary>Log lines as they arrive, oldest first. The view auto-scrolls to the newest.</summary>
        public ObservableCollection<string> Log { get; } = new();

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    RaisePropertyChanged(nameof(CanRun));
                    (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Whether Run has been clicked at least once -- switches the form from options to results.</summary>
        public bool HasRun
        {
            get => _hasRun;
            private set => SetProperty(ref _hasRun, value);
        }

        public bool CompletedOk
        {
            get => _completedOk;
            private set => SetProperty(ref _completedOk, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>Not running, and (see <see cref="ValidationError"/>) the options given so far make sense.</summary>
        public bool CanRun => !IsRunning && ValidationError is null;

        /// <summary>
        /// Why Run is disabled, or null if the current options are runnable. Re-checked by the view
        /// whenever a bound option property changes; subclasses call <see cref="NotifyOptionsChanged"/>
        /// from their option setters to trigger that.
        /// </summary>
        public abstract string? ValidationError { get; }

        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }

        public override bool CanExport => false;
        public override byte[] Export() => Array.Empty<byte>();

        /// <summary>
        /// Call from an option property's setter after <see cref="ObservableObject.SetProperty{T}"/> so
        /// the Run button's enabled state and any shown validation message stay current.
        /// </summary>
        protected void NotifyOptionsChanged()
        {
            RaisePropertyChanged(nameof(ValidationError));
            RaisePropertyChanged(nameof(CanRun));
            (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Do the work, reporting one line per notable event. Runs off the UI thread.</summary>
        protected abstract void Execute(IProgress<string> log, CancellationToken ct);

        private async void RunFireAndForget()
        {
            if (IsRunning || ValidationError is not null)
            {
                return;
            }

            Log.Clear();
            IsRunning = true;
            HasRun = true;
            CompletedOk = false;
            StatusMessage = "Running…";

            _cts = new CancellationTokenSource();
            // Progress<T> captures this constructor call's SynchronizationContext -- the UI thread's,
            // since RunFireAndForget only ever runs from a button click -- so every Report() marshals
            // back automatically. Log entries are safe to append to from here without further dispatch.
            var progress = new Progress<string>(line => Log.Add(line));
            CancellationToken ct = _cts.Token;

            try
            {
                await Task.Run(() => Execute(progress, ct), ct);
                StatusMessage = "Done.";
                CompletedOk = true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed: {ex.Message}";
                Log.Add($"ERROR: {ex}");
            }
            finally
            {
                IsRunning = false;
                _cts.Dispose();
                _cts = null;
            }
        }

        private void Cancel() => _cts?.Cancel();
    }
}
