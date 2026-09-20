namespace TokenStatus.Infrastructure.Codex;

public sealed class JsonRpcException : Exception
{
    public JsonRpcException(string message, int? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int? ErrorCode { get; }
}
