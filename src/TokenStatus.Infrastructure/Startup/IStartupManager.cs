namespace TokenStatus.Infrastructure.Startup;

public interface IStartupManager
{
    bool IsEnabled(string executablePath);
    void SetEnabled(string executablePath, bool enabled);
}
