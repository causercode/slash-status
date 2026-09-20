using System.Text;
using System.Text.RegularExpressions;

namespace TokenStatus.Infrastructure.Logging;

public sealed class RedactingFileLog : IRedactedLog
{
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumExcerptCharacters = 8 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;
    private readonly int _retainedFiles;
    private int _disposed;

    public RedactingFileLog(
        string? localApplicationData = null,
        int retainedFiles = 3)
    {
        var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(root, "TokenStatus", "logs");
        _filePath = Path.Combine(_directory, "token-status.log");
        _retainedFiles = Math.Max(1, retainedFiles);
        Directory.CreateDirectory(_directory);
    }

    public string LogDirectory => _directory;

    public void Information(
        string provider,
        string operation,
        string outcome,
        TimeSpan duration,
        string? diagnosticCode = null)
    {
        Write("INFO", provider, operation, outcome, duration, diagnosticCode, null, null);
    }

    public void Error(
        string provider,
        string operation,
        string diagnosticCode,
        Exception? exception = null,
        string? excerpt = null)
    {
        Write("ERROR", provider, operation, "error", null, diagnosticCode, exception, excerpt);
    }

    private void Write(
        string level,
        string provider,
        string operation,
        string outcome,
        TimeSpan? duration,
        string? diagnosticCode,
        Exception? exception,
        string? excerpt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var fields = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O"))
            .Append(" ").Append(level)
            .Append(" provider=").Append(Sanitize(provider, 64))
            .Append(" operation=").Append(Sanitize(operation, 96))
            .Append(" outcome=").Append(Sanitize(outcome, 32));

        if (duration is { } elapsed)
        {
            fields.Append(" durationMs=").Append((long)Math.Max(0, elapsed.TotalMilliseconds));
        }

        if (!string.IsNullOrWhiteSpace(diagnosticCode))
        {
            fields.Append(" code=").Append(Sanitize(diagnosticCode, 96));
        }

        if (exception is not null)
        {
            fields.Append(" exception=").Append(Sanitize(exception.GetType().Name, 96));
        }

        var safeExcerpt = Sanitize(excerpt, MaximumExcerptCharacters);
        if (!string.IsNullOrWhiteSpace(safeExcerpt))
        {
            fields.Append(" excerpt=").Append(safeExcerpt.ReplaceLineEndings(" "));
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            RotateIfNeeded();
            File.AppendAllText(_filePath, fields.AppendLine().ToString(), Encoding.UTF8);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length < MaximumFileBytes)
        {
            return;
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = _filePath + "." + index;
            var destination = _filePath + "." + (index + 1);
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(_filePath, _filePath + ".1", overwrite: true);
    }

    public static string Sanitize(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var capped = value.Length > maximumCharacters ? value[..maximumCharacters] : value;
        capped = Regex.Replace(capped, "(?i)(bearer\\s+)[^\\s]+", "$1[redacted]");
        capped = Regex.Replace(capped, "(?i)(token|secret|password|api[_-]?key)\\s*[:=]\\s*[^,;\\s]+", "$1=[redacted]");
        capped = Regex.Replace(capped, "(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", "[email-redacted]");
        capped = Regex.Replace(capped, "(?i)(account|organization|org)[_-]?id\\s*[:=]\\s*[^,;\\s]+", "$1Id=[redacted]");
        return capped.Replace('\r', ' ').Replace('\n', ' ');
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
