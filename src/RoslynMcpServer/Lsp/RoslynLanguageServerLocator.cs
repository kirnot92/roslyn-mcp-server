using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynLanguageServerLocator(CliOptions options)
{
    public const string InstallMessage =
        "roslyn-language-server was not found.\nInstall it with:\ndotnet tool install --global roslyn-language-server --prerelease";

    public string Locate()
    {
        if (!string.IsNullOrWhiteSpace(options.RoslynLanguageServerPath))
        {
            var explicitPath = Path.GetFullPath(options.RoslynLanguageServerPath);
            if (!File.Exists(explicitPath))
            {
                throw new UserFacingException("roslyn_language_server_not_found", $"{InstallMessage}\nConfigured path was not found: {explicitPath}");
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
