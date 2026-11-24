using System.Runtime.Versioning;
using Dirused.Common;
using Dirused.Cli;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public class ProgramScheduleRefusalTests
{
    [Fact]
    public void AcceptedNewFormsRefuseBeforeAnyPurgeFileSystemCall()
    {
        var forms = new (string[] Args, string Form)[]
        {
            (new[] { "/runplan", "id" }, "/runplan"),
            (new[] { "/schedule", "/list" }, "/schedule /list"),
            (new[] { "/schedule", "/cancel", "id" }, "/schedule /cancel"),
            (new[] { "/schedule", "/trust" }, "/schedule /trust"),
            (new[] { "/schedule", "/trust", "acct" }, "/schedule /trust"),
            (new[] { "/schedule", "/untrust", "acct" }, "/schedule /untrust")
        };
        foreach (var (args, form) in forms)
        {
            var fs = new CountingFileSystem();
            using var output = new StringWriter { NewLine = "\n" };
            using var error = new StringWriter { NewLine = "\n" };
            Assert.Equal(1, Program.RunWithPurge(args, output, error, Path.Combine(Path.GetTempPath(), "unused-purge.log"), fs));
            Assert.Empty(output.ToString());
            Assert.Equal(form + " is not available in this version\n", error.ToString());
            Assert.Equal(0, fs.Calls);
        }
    }

    private sealed class CountingFileSystem : IPurgeFileSystem
    {
        public int Calls { get; private set; }
        public string? ResolveRoot(string path, out string? error) { Calls++; error = null; return null; }
        public PurgeOpen Open(string path) { Calls++; return new PurgeOpen("unexpected"); }
    }
}
