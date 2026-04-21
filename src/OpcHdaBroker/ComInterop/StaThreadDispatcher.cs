// ═══════════════════════════════════════════════════════════════════════════
// COM THREAD DISPATCHER
// ───────────────────────────────────────────────────────────────────────────
// KepServerEX OPC HDA COM objects are created via the SDK which internally
// uses CoCreateInstance. The original hdatomqtt worked on the main thread
// (MTA). The QI for IOPCHDA_Server FAILS when called from an STA thread.
//
// This dispatcher uses a dedicated MTA thread instead, matching the original
// threading model that worked in hdatomqtt.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace OpcHdaBroker.ComInterop
{
    /// <summary>
    /// Runs all OPC HDA COM calls on a single dedicated thread.
    /// Uses MTA apartment (matching hdatomqtt's working threading model).
    /// API controllers queue work here and await the result.
    /// </summary>
    public sealed class StaThreadDispatcher : IDisposable
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<StaThreadDispatcher>();

        private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
        private readonly Thread _comThread;
        private bool _disposed;

        private class WorkItem
        {
            public Func<object> Work { get; set; }
            public TaskCompletionSource<object> Tcs { get; set; }
        }

        public StaThreadDispatcher()
        {
            _comThread = new Thread(ProcessQueue)
            {
                Name = "OPC-HDA-COM",
                IsBackground = true
            };
            // Use MTA — matches the threading model where QI works in hdatomqtt
            _comThread.SetApartmentState(ApartmentState.MTA);
            _comThread.Start();
            Log.Information("COM thread dispatcher started (thread {ThreadId}, MTA)", _comThread.ManagedThreadId);
        }

        /// <summary>
        /// Queue a function to run on the COM thread and await its result.
        /// All COM interop calls must go through this method.
        /// </summary>
        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StaThreadDispatcher));

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem
            {
                Work = () => work(),
                Tcs  = tcs
            });

            return tcs.Task.ContinueWith(t =>
            {
                if (t.IsFaulted) throw t.Exception.InnerException;
                return (T)t.Result;
            });
        }

        /// <summary>
        /// Queue a void action to run on the COM thread.
        /// </summary>
        public Task InvokeAsync(Action work)
        {
            return InvokeAsync<object>(() => { work(); return null; });
        }

        private void ProcessQueue()
        {
            Log.Debug("COM thread loop started (apartment={Apt})",
                Thread.CurrentThread.GetApartmentState());

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var result = item.Work();
                    item.Tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "COM call failed on COM thread");
                    item.Tcs.TrySetException(ex);
                }
            }
            Log.Debug("COM thread loop exited");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _comThread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
            Log.Information("COM thread dispatcher disposed");
        }
    }
}
