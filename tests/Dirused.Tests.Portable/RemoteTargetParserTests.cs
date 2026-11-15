using Dirused.Common.Targets;
using Xunit;
namespace Dirused.Tests.Portable;
public class RemoteTargetParserTests
{
    [Fact] public void ParsesTargetForms()
    {
        var unc = TargetParser.Parse("//server/share/path")!;
        Assert.Equal(TargetKind.Unc, unc.Kind); Assert.Equal(@"\\server\share\", unc.Root); Assert.Equal(@"\\server\share\path", unc.Path);
        Assert.Equal(TargetKind.Node, TargetParser.Parse("//node")!.Kind);
        foreach (var value in new[] { "C:", "c:", "d:/", @"D:\" }) Assert.Equal(TargetKind.LocalDrive, TargetParser.Parse(value)!.Kind);
        Assert.Equal(@"C:\", TargetParser.Parse("c:")!.Path);
        Assert.Equal(@"D:\apps\logs", TargetParser.Parse("d:/apps\\logs")!.Path);
        Assert.Equal(TargetKind.LocalPath, TargetParser.Parse("sub/dir")!.Kind);
        Assert.Equal(TargetKind.Unc, TargetParser.Parse(@"\\server\share")!.Kind);
        Assert.Equal(@"C:\x", TargetParser.Parse(@"C:\x")!.Path);
    }
}
