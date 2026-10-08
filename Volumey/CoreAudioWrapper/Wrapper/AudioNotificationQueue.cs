using System;
using System.Threading;
using System.Windows.Threading;
using log4net;

namespace Volumey.CoreAudioWrapper.Wrapper
{
    /// <summary>
    /// Core Audio callbacks must not block, call unregister, or release their last
    /// COM reference. Queue application work on the captured UI dispatcher instead.
    /// Always enqueue, even when the callback arrives on the UI thread.
    /// </summary>
    internal sealed class AudioNotificationQueue : IDisposable
    {
        private readonly Dispatcher dispatcher = App.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        private int disposed;

        internal void Post(Action action)
        {
            if(Volatile.Read(ref disposed) != 0 || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if(Volatile.Read(ref disposed) != 0)
                        return;
                    try { action(); }
                    catch(Exception e)
                    {
                        LogManager.GetLogger(typeof(AudioNotificationQueue)).Error("Failed to process audio notification", e);
                    }
                }), DispatcherPriority.Normal);
            }
            catch(InvalidOperationException) when(dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
        }

        internal bool TryDispose() => Interlocked.Exchange(ref disposed, 1) == 0;

        public void Dispose() => TryDispose();
    }
}
