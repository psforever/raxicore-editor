namespace RaxicoreEditor.Generation
{
    /// <summary>
    /// An <see cref="IProgress{T}"/> that calls back synchronously, in-thread.
    ///
    /// <see cref="Progress{T}"/> captures the calling thread's <see cref="SynchronizationContext"/> and
    /// marshals every report onto it -- and falls back to a thread-pool work item when there isn't one,
    /// which a console app never has. For a CLI wrapper writing lines straight to <c>Console.WriteLine</c>
    /// that means reports can arrive out of order, or after the caller has already returned. This is for
    /// exactly that case: a synchronous console/CLI consumer where in-order, same-thread delivery is the
    /// requirement, not an optimization.
    ///
    /// The Editor's own GUI callers use <see cref="Progress{T}"/> as usual -- there, marshaling onto the
    /// UI thread's <see cref="SynchronizationContext"/> is exactly what is wanted.
    /// </summary>
    public sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }
}
