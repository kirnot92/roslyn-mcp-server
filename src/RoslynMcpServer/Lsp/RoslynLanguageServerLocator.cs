using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynLanguageServerLocator(CliOptions options)
{
    public const string InstallMessage =
        """
        roslyn-language-server was not found.
        This server does not bundle roslyn-language-server; install it separately:
        dotnet tool install --global roslyn-language-server --prerelease
        The installed tool must be discoverable on PATH, or pass --roslyn-language-server <path>.
        Note: roslyn-language-server is currently prerelease and requires a .NET 10 runtime/SDK environment.
        """;

    public string Locate()
    {
        if (!string.IsNullOrWhiteSpace(options.RoslynLanguageServerPath))
        {
            var explicitPath = Path.GetFullPath(options.RoslynLanguageServerPath);
            if (!File.Exists(explicitPath))
            {
                throw new UserFacingException(
                    "roslyn_language_server_not_found",
                    $"{InstallMessage}{Environment.NewLine}Configured path was not found: {explicitPath}{Environment.NewLine}Fix the path passed to --roslyn-language-server, or remove the option and make roslyn-language-server available on PATH.");
            }

            return explicitPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, "roslyn-language-server" + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new UserFacingException("roslyn_language_server_not_found", InstallMessage);
    }
}
