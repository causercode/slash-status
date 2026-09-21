namespace TokenStatus.Infrastructure.Logging;

public static class RedactedLogFactory
{
    public static IRedactedLog Create(string? localApplicationData = null, int retainedFiles = 3)
    {
        try
        {
            return new RedactingFileLog(localApplicationData, retainedFiles);
        }
        catch (Exception exception) when (RedactingFileLog.IsFileSystemFailure(exception))
        {
            return new NullRedactedLog();
        }
    }

    public static string GetLogDirectory(string? localApplicationData = null)
    {
        try
        {
            var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "TokenStatus", "logs");
        }
        catch (Exception exception) when (RedactingFileLog.IsFileSystemFailure(exception))
        {
            return string.Empty;
        }
    }
}
