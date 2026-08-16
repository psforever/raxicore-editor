using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace RaxicoreEditor.Editor.Controls
{
    /// <summary>
    /// Attach to a <see cref="ScrollViewer"/> to keep it scrolled to the bottom while a bound
    /// <see cref="INotifyCollectionChanged"/> log keeps growing -- e.g. a Generate tab's output.
    ///
    /// Usage: <c>&lt;ScrollViewer controls:AutoScrollLog.Source="{Binding Log}"&gt;</c>.
    /// </summary>
    public static class AutoScrollLog
    {
        public static readonly AttachedProperty<IEnumerable?> SourceProperty =
            AvaloniaProperty.RegisterAttached<ScrollViewer, IEnumerable?>("Source", typeof(AutoScrollLog));

        static AutoScrollLog()
        {
            SourceProperty.Changed.AddClassHandler<ScrollViewer>(OnSourceChanged);
        }

        public static void SetSource(ScrollViewer element, IEnumerable? value) =>
            element.SetValue(SourceProperty, value);

        public static IEnumerable? GetSource(ScrollViewer element) =>
            element.GetValue(SourceProperty);

        // Keyed by the ScrollViewer instance (weakly -- a plain Dictionary would hold every ScrollViewer
        // this has ever attached to for the rest of the process, since the key itself is a strong
        // reference). Also lets the handler used to subscribe be the same one used to unsubscribe later;
        // a freshly-made closure would never match the delegate registered with -=.
        private static readonly ConditionalWeakTable<ScrollViewer, NotifyCollectionChangedEventHandler> Handlers = new();

        private static void OnSourceChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyCollectionChanged oldIncc && Handlers.TryGetValue(viewer, out var old))
            {
                oldIncc.CollectionChanged -= old;
                Handlers.Remove(viewer);
            }
            if (e.NewValue is INotifyCollectionChanged newIncc)
            {
                NotifyCollectionChangedEventHandler handler = (_, _) => viewer.ScrollToEnd();
                Handlers.AddOrUpdate(viewer, handler);
                newIncc.CollectionChanged += handler;
            }
        }
    }
}
