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
    int MaxInFlightLspRequests,
    int MaxExpensiveLspRequests)
{
    public const int DefaultScanMaxDepth = 6;
    public const int DefaultMaxOpenDocuments = 200;
    public const long DefaultMaxDocumentBytes = 2 * 1024 * 1024;
    public const int DefaultMaxExpensiveLspRequests = 2;
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
        var scanMaxDepth = DefaultScanMaxDepth;
        var scanTimeout = DefaultScanTimeout;
        var maxSolutionCandidates = 100;
        var maxProjectCandidates = 500;
        var maxOpenDocuments = DefaultMaxOpenDocuments;
        var maxDocumentBytes = DefaultMaxDocumentBytes;
        var maxInFlightLspRequests = 16;
        var maxExpensiveLspRequests = DefaultMaxExpensiveLspRequests;

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
                case "--scan-max-depth":
                    scanMaxDepth = ParseNonNegativeInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--scan-timeout":
                    scanTimeout = ParseTimeout(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-solution-candidates":
                    maxSolutionCandidates = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-project-candidates":
                    maxProjectCandidates = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-open-documents":
                    maxOpenDocuments = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-document-bytes":
                    maxDocumentBytes = ParsePositiveLong(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-in-flight-lsp-requests":
                    maxInFlightLspRequests = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                    break;
                case "--max-expensive-lsp-requests":
                    maxExpensiveLspRequests = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
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
            scanMaxDepth,
            scanTimeout,
            maxSolutionCandidates,
            maxProjectCandidates,
            maxOpenDocuments,
            maxDocumentBytes,
            maxInFlightLspRequests,
            maxExpensiveLspRequests);
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
          --scan-max-depth <depth>
          --scan-timeout <seconds>
          --max-solution-candidates <count>
          --max-project-candidates <count>
          --max-open-documents <count>
          --max-document-bytes <bytes>
          --max-in-flight-lsp-requests <count>
          --max-expensive-lsp-requests <count>
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

    private static int ParseNonNegativeInt(string value, string optionName)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new CliUsageException($"{optionName} must be a non-negative integer.");
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
