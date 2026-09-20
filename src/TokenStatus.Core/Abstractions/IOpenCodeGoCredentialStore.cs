namespace TokenStatus.Core.Abstractions;

public interface IOpenCodeGoCredentialStore
{
    bool IsConfigured();
    string? ReadApiKey();
    void SaveApiKey(string apiKey);
    void DeleteApiKey();
}
