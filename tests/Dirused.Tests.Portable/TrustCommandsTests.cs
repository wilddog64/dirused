using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public sealed class TrustCommandsTests
{
    private const string AdminSid = ScheduleFakes.AdminSid;
    private static (FakeTrustStore Store, FakeAccountResolver Resolver, StringWriter Out, StringWriter Error) Setup()
    {
        var calls = new List<string>(); var store = new FakeTrustStore(calls); var resolver = new FakeAccountResolver(calls);
        resolver.Accounts[@"PROD\admin2"] = new(AdminSid, @"PROD\admin2");
        resolver.Accounts["Everyone"] = new("S-1-1-0", "Everyone");
        return (store, resolver, new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
    }
    [Fact] public void ListingIsSortedAndEmptyMessageIsExact()
    {
        var x = Setup(); Assert.Equal(0, TrustCommands.Trust(null, x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("No trusted accounts.\n", x.Out.ToString());
        x.Store.Accounts.Add(new("S2", "zeta")); x.Store.Accounts.Add(new("S1", "Alpha")); x.Out.GetStringBuilder().Clear();
        TrustCommands.Trust(null, x.Store, x.Resolver, x.Out, x.Error); Assert.Equal("  S1  Alpha\n  S2  zeta\n", x.Out.ToString());
    }
    [Fact] public void TrustAndUntrustResolveDeduplicateAndReportErrors()
    {
        var x = Setup();
        Assert.Equal(0, TrustCommands.Trust(@"PROD\admin2", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("Trusted PROD\\admin2 (" + AdminSid + ").\n", x.Out.ToString()); Assert.Contains(x.Store.Accounts, a => a.Sid == AdminSid);
        x.Out.GetStringBuilder().Clear(); Assert.Equal(0, TrustCommands.Trust(@"PROD\admin2", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("PROD\\admin2 is already trusted.\n", x.Out.ToString());
        x.Out.GetStringBuilder().Clear(); Assert.Equal(0, TrustCommands.Trust(ScheduleFakes.UnknownSid, x.Store, x.Resolver, x.Out, x.Error)); Assert.Contains("unresolved account " + ScheduleFakes.UnknownSid, x.Store.Accounts.Last().Display);
        Assert.Equal(1, TrustCommands.Trust("bob", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("unknown account: bob\n", x.Error.ToString());
        x.Error.GetStringBuilder().Clear(); Assert.Equal(1, TrustCommands.Trust("Everyone", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("Everyone is too wide to trust\n", x.Error.ToString()); Assert.DoesNotContain(x.Store.Accounts, a => a.Sid == "S-1-1-0");
        x.Store.Failure = "disk full"; x.Error.GetStringBuilder().Clear(); Assert.Equal(3, TrustCommands.Trust("S-1-5-21-1-2-3-3000", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("error: could not save the trust list: disk full\n", x.Error.ToString());
        x.Store.Failure = null; x.Out.GetStringBuilder().Clear(); Assert.Equal(0, TrustCommands.Untrust(@"PROD\admin2", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("Removed PROD\\admin2.\n", x.Out.ToString());
        x.Error.GetStringBuilder().Clear(); Assert.Equal(1, TrustCommands.Untrust("bob", x.Store, x.Resolver, x.Out, x.Error)); Assert.Equal("bob is not trusted.\n", x.Error.ToString());
    }
}
