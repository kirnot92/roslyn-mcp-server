namespace RoslynMcpServer.Infrastructure;

public sealed class UserFacingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
