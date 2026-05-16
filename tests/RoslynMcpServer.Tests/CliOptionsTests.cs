using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_AcceptsLargeRepoTuningOptions()
    {
        using var root = TestRoot.Create();

        var options = CliOptions.Parse(
        [
            "--root", root.Path,
            "--scan-max-depth", "3",
            "--scan-timeout", "1.5",
            "--max-solution-candidates", "7",
            "--max-project-candidates", "11",
            "--max-in-flight-lsp-requests", "5"
        ]);

        Assert.Equal(3, options.ScanMaxDepth);
        Assert.Equal(TimeSpan.FromSeconds(1.5), options.ScanTimeout);
        Assert.Equal(7, options.MaxSolutionCandidates);
        Assert.Equal(11, options.MaxProjectCandidates);
        Assert.Equal(5, options.MaxInFlightLspRequests);
    }

    [Fact]
    public void Parse_RejectsInvalidLargeRepoTuningOption()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["--max-in-flight-lsp-requests", "0"]));

        Assert.Contains("--max-in-flight-lsp-requests must be a positive integer", ex.Message);
    }
}
