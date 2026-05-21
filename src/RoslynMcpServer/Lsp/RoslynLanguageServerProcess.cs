using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynLanguageServerProcess(
    CliOptions options,
    ILogger<RoslynLanguageServerProcess> logger,
    ILoggerFactory loggerFactory)
{
    public const string InstallMessage =
        """
        roslyn-language-server was not found.
        This server does not bundle roslyn-language-server; install it separately:
        dotnet tool install --global roslyn-language-server --prerelease
        The installed tool must be discoverable on PATH, or pass --roslyn-language-server <path>.
        Note: roslyn-language-server is currently prerelease and requires a .NET 10 runtime/SDK environment.
        """;

    public static string LocateExecutable(CliOptions options)
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

    public RoslynWorkspaceHandle Start(WorkspaceTarget target)
    {
        var connection = StartConnection(target.WorkspaceDirectory);
        return new RoslynWorkspaceHandle(target, connection);
    }

    private RoslynLanguageServerConnection StartConnection(string workingDirectory)
    {
        var executable = LocateExecutable(options);
        var startInfo = CreateStartInfo(executable, workingDirectory);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start roslyn-language-server.");
        }

        _ = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                logger.LogInformation("{Line}", line);
            }
        });

        var client = new LspClient(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            options.MaxInFlightLspRequests,
            options.MaxExpensiveLspRequests,
            loggerFactory.CreateLogger<LspClient>());
        client.Start();

        return new RoslynLanguageServerConnection(process, client);
    }

    private ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
    {
        var arguments = new List<string>
        {
            "--stdio",
            "--logLevel",
            ToLanguageServerLogLevel(options.LogLevel)
        };

        if (!string.IsNullOrWhiteSpace(options.LanguageServerLogDirectory))
        {
            Directory.CreateDirectory(options.LanguageServerLogDirectory);
            arguments.Add("--extensionLogDirectory");
            arguments.Add(options.LanguageServerLogDirectory);
        }

        if (OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(executable);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        var direct = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            direct.ArgumentList.Add(argument);
        }

        return direct;
    }

    private static string ToLanguageServerLogLevel(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => "Information"
        };
}

public sealed class RoslynLanguageServerConnection(Process process, LspClient client) : IAsyncDisposable
{
    public Process Process { get; } = process;
    public LspClient Client { get; } = client;
    public bool IsRunning => !Process.HasExited;

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            try
            {
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                try
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                    // Best-effort cleanup. Shutdown is attempted before disposal by WorkspaceSession.
                }
            }
        }

        await Client.DisposeAsync();
        Process.Dispose();
    }
}
