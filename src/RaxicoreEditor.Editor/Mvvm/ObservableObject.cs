using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RaxicoreEditor.Editor.Mvvm
{
    /// <summary>Minimal hand-rolled INotifyPropertyChanged base (no source generators).</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            RaisePropertyChanged(name);
            return true;
        }
    }
}
