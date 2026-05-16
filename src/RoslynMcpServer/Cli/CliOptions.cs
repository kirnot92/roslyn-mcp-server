using Microsoft.Extensions.Logging;

namespace RoslynMcpServer.Cli;

public sealed record CliOptions(
    string Root,
    string? RoslynLanguageServerPath,
    LogLevel LogLevel,
    string? LogFile,
    string? LanguageServerLogDirectory,
    TimeSpan StartupTimeout,
    int ScanMaxDepth,
    TimeSpan ScanTimeout,
    int MaxSolutionCandidates,
    int MaxProjectCandidates,
    int MaxOpenDocuments,
    long MaxDocumentBytes,
    int MaxInFlightLspRequests)
{
    public const int DefaultScanMaxDepth = 6;
    public const int DefaultMaxOpenDocuments = 200;
    public const long DefaultMaxDocumentBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(60);

    public static CliOptions Parse(string[] args)
    {
        string? root = null;
        string? roslynLanguageServerPath = null;
        var logLevel = LogLevel.Information;
        string? logFile = null;
        string? languageServerLogDirectory = null;
        var startupTimeout = DefaultStartupTimeout;
        var maxOpenDocuments = DefaultMaxOpenDocuments;
        var maxDocumentBytes = DefaultMaxDocumentBytes;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--root":
                    root = ReadValue(args, ref i, arg);
                    break;
                case "--roslyn-language-server":
                    roslynLanguageServerPath = ReadValue(args, ref i, arg);
                    break;
                case "--log-level":
                    logLevel = ParseLogLevel(ReadValue(args, ref i, arg));
                    break;
                case "--log-file":
                    logFile = ReadValue(args, ref i, arg);
                    break;
                case "--ls-log-dir":
                    languageServerLogDirectory = ReadValue(args, ref i, arg);
                    break;
                case "--startup-timeout":
                    startupTimeout = ParseTimeout(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-open-documents":
                    maxOpenDocuments = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-document-bytes":
                    maxDocumentBytes = ParsePositiveLong(ReadValue(args, ref i, arg), arg);
                    break;
                case "-h":
                case "--help":
                    throw new CliUsageException(Usage);
                default:
                    throw new CliUsageException($"Unknown option '{arg}'.{Environment.NewLine}{Usage}");
            }
        }

        var normalizedRoot = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        return new CliOptions(
            normalizedRoot,
            roslynLanguageServerPath,
            logLevel,
            logFile is null ? null : Path.GetFullPath(logFile),
            languageServerLogDirectory is null ? null : Path.GetFullPath(languageServerLogDirectory),
            startupTimeout,
            DefaultScanMaxDepth,
            DefaultScanTimeout,
            MaxSolutionCandidates: 100,
            MaxProjectCandidates: 500,
            maxOpenDocuments,
            maxDocumentBytes,
            MaxInFlightLspRequests: 16);
    }

    public static string Usage =>
        """
        roslyn-mcp-server
          --root <path>
          --roslyn-language-server <path>
          --log-level <trace|debug|info|warn|error>
          --log-file <path>
          --ls-log-dir <path>
          --startup-timeout <seconds>
          --max-open-documents <count>
          --max-document-bytes <bytes>
        """;

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new CliUsageException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static TimeSpan ParseTimeout(string value, string optionName)
    {
        if (!double.TryParse(value, out var seconds) || seconds <= 0)
        {
            throw new CliUsageException($"{optionName} must be a positive number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static int ParsePositiveInt(string value, string optionName)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new CliUsageException($"{optionName} must be a positive integer.");
        }

        return parsed;
    }

    private static long ParsePositiveLong(string value, string optionName)
    {
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new CliUsageException($"{optionName} must be a positive integer.");
        }

        return parsed;
    }

    private static LogLevel ParseLogLevel(string value) =>
        value.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => throw new CliUsageException("--log-level must be one of trace, debug, info, warn, error.")
        };
}

public sealed class CliUsageException(string message) : Exception(message);
