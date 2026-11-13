using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenPdfStudio.Services;

/// <summary>
/// Keeps one OpenPdfStudio process. A second launch sends PDF paths to the first and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OpenPdfStudio.SingleInstance";
    private const string PipeName = "OpenPdfStudio.Instance";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action<string>? Received;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _ = ListenAsync();
    }

    public static bool TryClaim(out SingleInstance? instance)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public const string ActivateToken = "__ACTIVATE__";
    public const string CompareTokenPrefix = "__COMPARE__|";

    public static void Forward(IEnumerable<string> args)
    {
        var paths = PdfLaunch.PathsFromArgs(args);
        var compareLine = PdfLaunch.TryGetComparePair(args, out var left, out var right)
            ? $"{CompareTokenPrefix}{left}|{right}"
            : null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivateToken);
                if (compareLine is not null)
                    writer.WriteLine(compareLine);
                foreach (var path in paths)
                    writer.WriteLine(path);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Received?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was abandoned if this process is shutting down after a crash.
        }
        _mutex.Dispose();
    }
}
