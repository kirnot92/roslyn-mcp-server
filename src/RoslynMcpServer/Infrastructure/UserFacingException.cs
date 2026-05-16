namespace RoslynMcpServer.Infrastructure;

public sealed class UserFacingException : Exception
{
    public UserFacingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public UserFacingException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
