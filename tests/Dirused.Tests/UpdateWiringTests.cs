using Dirused.Cli;
using Dirused.Common.Update;
using Xunit;

namespace Dirused.Tests;

public sealed class UpdateWiringTests
{
    [Fact]
    public void W1_RunWithReleaseSourcePrintsNoticeToErrorOnly()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dirused-w1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Assert.Equal(scratch, Path.GetFullPath(scratch));
        var state = Path.Combine(scratch, "update-check.json");
        var output = new StringWriter(); var error = new StringWriter(); var source = new FakeSource();
        var prior = Environment.GetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK"); Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", null);
        try
        {
            var rc = Program.RunWithReleaseSource(new[] { scratch }, output, error, source, "0.0.10", state);
            Assert.Equal(0, rc); Assert.Contains("dirused 0.0.11 is available", error.ToString()); Assert.DoesNotContain("available", output.ToString()); Assert.Equal(1, source.FindCalls); Assert.Equal(0, source.DownloadCalls);
        }
        finally { Environment.SetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK", prior); Directory.Delete(scratch, true); }
    }

    [Fact]
    public void W2_ExistingProgramRunOverloadDoesNotCallReleaseSource()
    {
        var source = new FakeSource(); var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "/help" }, output, error));
        Assert.Equal(0, source.FindCalls); Assert.Equal(0, source.DownloadCalls);
    }

    [Fact]
    public void W3_BareWordsRemainScanTargetsAndPrefixedVersionWorks()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dirused-w3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        Assert.Equal(scratch, Path.GetFullPath(scratch));
        try
        {
            foreach (var word in new[] { "update", "version" })
            {
                var output = new StringWriter(); var error = new StringWriter(); var source = new FakeSource();
                Program.RunWithReleaseSource(new[] { word, "/json" }, output, error, source, "0.0.10", Path.Combine(scratch, word + ".json"));
                Assert.Contains(word, output.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("dirused 0.0.", output.ToString()); Assert.Equal(0, source.DownloadCalls);
            }
            var versionOutput = new StringWriter(); Assert.Equal(0, Program.RunWithReleaseSource(new[] { "/version" }, versionOutput, new StringWriter(), new FakeSource(), "0.0.10"));
            Assert.Contains("dirused 0.0.10", versionOutput.ToString());
        }
        finally { Directory.Delete(scratch, true); }
    }

    private sealed class FakeSource : IReleaseSource
    {
        public int FindCalls, DownloadCalls;
        public Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult<ReleasePackage?>(new("0.0.11", "v0.0.11", rid,
                new Uri("https://github.com/wilddog64/dirused/releases/download/v0.0.11/test.zip"),
                new Uri("https://github.com/wilddog64/dirused/releases/download/v0.0.11/test.zip.sha256"), "test.zip"));
        }
        public Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default) { DownloadCalls++; throw new InvalidOperationException("scan must not download assets"); }
    }
}
