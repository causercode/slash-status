using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.Codex;

public sealed class JsonLineRpcConnection : IAsyncDisposable
{
    public const int MaximumLineCharacters = 4 * 1024 * 1024;

    private readonly string _executablePath;
    private readonly string _clientVersion;
    private readonly IReadOnlyList<string> _processArguments;
    private readonly IRedactedLog? _log;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumLineCharacters;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _processGate = new();
    private Process? _process;
    private StreamWriter? _input;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _readerTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private int _disposed;

    public JsonLineRpcConnection(
        string executablePath,
        string clientVersion,
        IRedactedLog? log = null,
        TimeSpan? requestTimeout = null,
        IReadOnlyList<string>? processArguments = null,
        int maximumLineCharacters = MaximumLineCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCharacters);
        _executablePath = Path.GetFullPath(executablePath);
        _clientVersion = clientVersion;
        _log = log;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _processArguments = processArguments ?? ["app-server", "--listen", "stdio://"];
        _maximumLineCharacters = maximumLineCharacters;
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextRequestId);
        return await SendRequestAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning())
            {
                return;
            }

            StopProcess(new IOException("Codex app-server is not running."));
            StartProcess();

            var initializeParameters = new
            {
                clientInfo = new
                {
                    name = "token_status",
                    title = "/status",
                    version = _clientVersion
                },
                capabilities = new
                {
                    optOutNotificationMethods = new[]
                    {
                        "thread/started",
                        "item/agentMessage/delta"
                    }
                }
            };

            await SendRequestAsync(0, "initialize", initializeParameters, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            StopProcess(new IOException("Codex app-server initialization failed."));
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in _processArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Codex app-server could not be started.");
        }

        lock (_processGate)
        {
            _process = process;
            _input = process.StandardInput;
            _connectionCancellation = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoopAsync(process, _connectionCancellation.Token));
            _stderrTask = Task.Run(() => DrainStderrAsync(process, _connectionCancellation.Token));
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        long id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, source))
        {
            throw new InvalidOperationException("The JSON-RPC request ID was already in use.");
        }

        try
        {
            await SendLineAsync(new { method, id, @params = parameters }, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _connectionCancellation?.Token ?? CancellationToken.None);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                return await source.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (source.Task.IsCompleted)
                {
                    return await source.Task.ConfigureAwait(false);
                }

                throw new TimeoutException("The Codex app-server request timed out.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await SendLineAsync(new { method, @params = parameters }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLineAsync(object message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter input;
            lock (_processGate)
            {
                input = _input ?? throw new IOException("Codex app-server input is unavailable.");
            }

            var line = JsonSerializer.Serialize(message);
            await input.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        var lineReader = new BoundedAsyncLineReader(process.StandardOutput, _maximumLineCharacters);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await lineReader.ReadBoundedLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new EndOfStreamException("Codex app-server stdout closed.");
                }

                using var document = JsonDocument.Parse(line, new JsonDocumentOptions
                {
                    MaxDepth = 64,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) ||
                    !idElement.TryGetInt64(out var id) ||
                    !_pending.TryGetValue(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                        ? parsedCode
                        : (int?)null;
                    var message = error.TryGetProperty("message", out var messageElement) &&
                                  messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : "Codex app-server returned an error.";
                    completion.TrySetException(new JsonRpcException(message ?? "Codex app-server returned an error.", code));
                    continue;
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    completion.TrySetException(new JsonRpcException("Codex app-server response did not contain a result."));
                    continue;
                }

                completion.TrySetResult(result.Clone());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log?.Error("codex", "jsonl_reader", "connection_closed", exception);
            StopProcess(exception);
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[1024];
            var retained = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await process.StandardError.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                retained = Math.Min(8 * 1024, retained + read);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log?.Error("codex", "stderr", "stderr_read_failed", exception);
        }
    }

    private bool IsRunning()
    {
        lock (_processGate)
        {
            return _process is { HasExited: false };
        }
    }

    private void StopProcess(Exception reason)
    {
        Process? process;
        CancellationTokenSource? cancellation;
        lock (_processGate)
        {
            process = _process;
            cancellation = _connectionCancellation;
            _process = null;
            _input = null;
            _connectionCancellation = null;
            _readerTask = null;
            _stderrTask = null;
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(reason);
        }
        cancellation?.Cancel();

        if (process is null)
        {
            cancellation?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
            cancellation?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess(new ObjectDisposedException(nameof(JsonLineRpcConnection)));
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
            _writeGate.Dispose();
        }
    }
}
