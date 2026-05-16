using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynLanguageServerProcess(
    CliOptions options,
    RoslynLanguageServerLocator locator,
    ILogger<RoslynLanguageServerProcess> logger,
    ILoggerFactory loggerFactory)
{
    public RoslynLanguageServerConnection Start(string workingDirectory)
    {
        var executable = locator.Locate();
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
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
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
            loggerFactory.CreateLogger<LspClient>());
        client.Start();

        return new RoslynLanguageServerConnection(process, client);
    }

    private ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
    {
        var arguments = new List<string>
        {
            "--stdio",
            "--autoLoadProjects",
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
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort cleanup. Shutdown is attempted before disposal by WorkspaceSession.
                }
            }
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
    }
}
