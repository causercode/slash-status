using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;

namespace TokenStatus.Infrastructure.Cli;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

public sealed class ProcessRunner : IDisposable
{
    public const int DefaultMaximumStandardOutputCharacters = 2 * 1024 * 1024;
    public const int DefaultMaximumStandardErrorCharacters = 8 * 1024;

    private readonly ConcurrentDictionary<int, Process> _children = new();
    private int _disposed;

    public async Task<ProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int maximumStandardOutputCharacters = DefaultMaximumStandardOutputCharacters,
        int maximumStandardErrorCharacters = DefaultMaximumStandardErrorCharacters)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("The child process could not be started.");
        }

        _children[process.Id] = process;
        try
        {
            process.StandardInput.Close();
            var stdoutTask = ReadCappedAsync(process.StandardOutput, maximumStandardOutputCharacters, cancellationToken);
            var stderrTask = ReadCappedAsync(process.StandardError, maximumStandardErrorCharacters, cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);

            var timedOut = completed == timeoutTask && !waitTask.IsCompleted;
            if (timedOut)
            {
                TryKill(process);
                await waitTask.ConfigureAwait(false);
            }
            else
            {
                await waitTask.ConfigureAwait(false);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                timedOut,
                stdout.Truncated,
                stderr.Truncated);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            _children.TryRemove(process.Id, out _);
        }
    }

    private static async Task<CappedText> ReadCappedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                truncated = true;
            }
        }

        return new CappedText(builder.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var process in _children.Values)
        {
            TryKill(process);
        }

        _children.Clear();
    }

    private readonly record struct CappedText(string Text, bool Truncated);
}
