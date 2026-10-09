using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>
/// Runs every PDFium call on one dedicated thread. PDFium is not thread-safe, so no other
/// thread may touch a PDFium handle.
/// </summary>
public sealed class PdfiumWorker : IDisposable
{
    private static readonly Lazy<PdfiumWorker> LazyInstance = new(() => new PdfiumWorker());

    private readonly object _gate = new();
    private readonly Queue<Action> _high = new();
    private readonly Queue<Action> _low = new();
    private readonly Thread _thread;
    private bool _stopping;

    public static PdfiumWorker Instance => LazyInstance.Value;

    private PdfiumWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PDFium" };
        _thread.Start();
    }

    public static void Shutdown()
    {
        if (LazyInstance.IsValueCreated)
            LazyInstance.Value.Dispose();
    }

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default, bool lowPriority = false)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Job()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        lock (_gate)
        {
            if (_stopping)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(PdfiumWorker)));
                return tcs.Task;
            }

            (lowPriority ? _low : _high).Enqueue(Job);
            Monitor.Pulse(_gate);
        }

        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default, bool lowPriority = false) =>
        InvokeAsync(() =>
        {
            work();
            return true;
        }, cancellationToken, lowPriority);

    private void Run()
    {
        fpdfview.FPDF_InitLibrary();

        while (true)
        {
            Action job;
            lock (_gate)
            {
                while (_high.Count == 0 && _low.Count == 0 && !_stopping)
                    Monitor.Wait(_gate);

                if (_high.Count == 0 && _low.Count == 0)
                    break;

                job = _high.Count > 0 ? _high.Dequeue() : _low.Dequeue();
            }

            job();
        }

        fpdfview.FPDF_DestroyLibrary();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
