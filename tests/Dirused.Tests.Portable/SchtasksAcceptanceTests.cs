using System.Text;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public class SchtasksAcceptanceTests
{
    private const string Sys = "C:\\Windows\\System32\\";
    private const string Exe = "C:\\Windows\\System32\\schtasks.exe";
    private const string Name = "\\dirused\\S-1-5-21-1-2-3-1001\\purge-20261125-1533-9a1f";
    private const string XmlPath = "C:\\plans\\20261125-1533-9a1f\\task.xml";
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Dir = "C:\\plans\\20261125-1533-9a1f";

    private sealed class Runner(Func<int, IReadOnlyList<string>, ProcessResult> f) : IProcessRunner
    {
        public readonly List<(string Exe, string[] Args)> Calls = new();
        public ProcessResult Run(string exe, IReadOnlyList<string> args)
        {
            Calls.Add((exe, args.ToArray()));
            return f(Calls.Count - 1, args);
        }
    }

    private static PlanTask Expected() => new(
        "20261125-1533-9a1f",
        new DateTimeOffset(2026, 11, 25, 15, 33, 0, TimeSpan.FromHours(-8)),
        new DateTimeOffset(2026, 11, 26, 15, 33, 0, TimeSpan.FromHours(-8)),
        Sid, Dir, "dirused scheduled purge 20261125-1533-9a1f (1 file(s), 1.0K) from a.txt");

    private static string Text(byte[] b) => Encoding.Unicode.GetString(b, 2, b.Length - 2);
    private static byte[] U16(string s) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)];

    private static byte[] Mut(Func<string, string> m)
    {
        var t = Text(TaskXml.Build(Expected()));
        var r = m(t);
        Assert.NotEqual(t, r);
        return U16(r);
    }

    private static string Rep(string t, string a, string b)
    {
        Assert.Contains(a, t);
        return t.Replace(a, b);
    }

    private static (SeamResult R, Runner Rn) Reg(byte[] readback, int create = 0, int query = 0, int del = 0)
    {
        var rn = new Runner((i, a) => i switch
        {
            0 => new ProcessResult(create, []),
            1 => new ProcessResult(query, readback),
            _ => new ProcessResult(del, [])
        });
        var r = new SchtasksTaskScheduler(rn, Sys).Register(Name, XmlPath, Expected());
        return (r, rn);
    }

    [Fact]
    public void M1_ArgvAndOk()
    {
        var (r, rn) = Reg(TaskXml.Build(Expected()));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, rn.Calls.Count);
        Assert.All(rn.Calls, c => Assert.Equal(Exe, c.Exe));
        Assert.Equal(["/create", "/tn", Name, "/xml", XmlPath], rn.Calls[0].Args);
        Assert.Equal(["/query", "/tn", Name, "/xml"], rn.Calls[1].Args);
    }

    [Fact]
    public void M1_SystemDirectoryWithoutTrailingSlash()
    {
        var rn = new Runner((i, a) => new ProcessResult(0, i == 1 ? TaskXml.Build(Expected()) : []));
        new SchtasksTaskScheduler(rn, "C:\\Windows\\System32").Register(Name, XmlPath, Expected());
        Assert.Equal(Exe, rn.Calls[0].Exe);
    }

    [Fact]
    public void M1_SameInstantPlus00Readback()
    {
        var e = Expected();
        var t = Text(TaskXml.Build(e));
        var r = t.Replace("2026-11-25T15:33:00-08:00", "2026-11-25T23:33:00+00:00").Replace("2026-11-26T15:33:00-08:00", "2026-11-26T23:33:00+00:00");
        Assert.NotEqual(t, r);
        var (res, rn) = Reg(U16(r));
        Assert.True(res.Ok, res.Error);
        Assert.Equal(2, rn.Calls.Count);
    }

    private static string Shape(string t)
    {
        foreach (var tag in new[] { "<Enabled>true</Enabled>", "<AllowHardTerminate>true</AllowHardTerminate>", "<StartWhenAvailable>false</StartWhenAvailable>",
                     "<Hidden>false</Hidden>", "<RunLevel>LeastPrivilege</RunLevel>", "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" })
            t = Rep(t, tag, "");
        t = Rep(t, "</Description>", "</Description><URI>\\dirused\\x</URI>");
        t = Rep(t, "</Settings>", "<IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>");
        t = t.Replace("><", ">\r\n<");
        return t;
    }

    [Fact]
    public void M1_P9ReadbackShape()
    {
        var t = Shape(Text(TaskXml.Build(Expected())));
        t = t.Replace("2026-11-25T15:33:00-08:00", "2026-11-25T23:33:00+00:00").Replace("2026-11-26T15:33:00-08:00", "2026-11-26T23:33:00+00:00");
        var bytes = Encoding.ASCII.GetBytes(t);
        Assert.Contains("encoding=\"UTF-16\"", t);
        var (r, rn) = Reg(bytes);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, rn.Calls.Count);
    }

    public static IEnumerable<object[]> Rows()
    {
        yield return Row("AllowStartOnDemand", t => Rep(t, "<AllowStartOnDemand>false", "<AllowStartOnDemand>true"));
        yield return Row("StartWhenAvailable", t => Rep(t, "<StartWhenAvailable>false", "<StartWhenAvailable>true"));
        yield return Row("LogonType", t => Rep(t, "InteractiveToken", "Password"));
        yield return Row("RunLevel", t => Rep(t, "LeastPrivilege", "HighestAvailable"));
        yield return Row("ExecutionTimeLimit", t => Rep(t, "PT2H", "PT9H"));
        yield return Row("DisallowStartIfOnBatteries", t => Rep(t, "<DisallowStartIfOnBatteries>false", "<DisallowStartIfOnBatteries>true"));
        yield return Row("Hidden", t => Rep(t, "<Hidden>false", "<Hidden>true"));
        yield return Row("Id", t => Rep(t, "/runplan 20261125-1533-9a1f", "/runplan 20261125-1534-9a1f"));
        yield return Row("Arguments", t => Rep(t, "/runplan 20261125-1533-9a1f", "/runplan whatever"));
        yield return Row("Run", t => Rep(t, "2026-11-25T15:33:00-08:00", "2026-11-25T15:34:00-08:00"));
        yield return Row("Expires", t => Rep(t, "2026-11-26T15:33:00-08:00", "2026-11-26T15:34:00-08:00"));
        yield return Row("UserSid", t => Rep(t, "<UserId>" + Sid, "<UserId>S-1-5-21-1-2-3-1002"));
        yield return Row("PlanDir", t => Rep(Rep(t, "<Command>" + Dir, "<Command>C:\\other"), "<WorkingDirectory>" + Dir, "<WorkingDirectory>C:\\other"));
        yield return Row("Command", t => Rep(t, "<Command>" + Dir + "\\dirused.exe", "<Command>" + Dir + "\\evil.exe"));
        yield return Row("Description", t => Rep(t, "from a.txt", "from b.txt"));
        yield return Row("SecurityDescriptor", t => Rep(t, "(A;;FA;;;BA)", "(A;;FA;;;BA)(A;;FA;;;WD)"));
    }

    private static object[] Row(string f, Func<string, string> m) => [f, m];

    [Theory]
    [MemberData(nameof(Rows))]
    public void M2_FieldTable(string field, Func<string, string> m)
    {
        var (r, rn) = Reg(Mut(m));
        Assert.False(r.Ok);
        Assert.Equal($"scheduled task does not match the plan ({field}); it was removed", r.Error);
        Assert.Equal(3, rn.Calls.Count);
        Assert.Equal(["/delete", "/tn", Name, "/f"], rn.Calls[2].Args);
        Assert.Equal(Exe, rn.Calls[2].Exe);
    }

    [Fact]
    public void M2_CaseOnlySidAndDirAreOk()
    {
        var (r, _) = Reg(Mut(t => Rep(Rep(t, "<Command>" + Dir, "<Command>" + Dir.ToUpperInvariant()), "<WorkingDirectory>" + Dir, "<WorkingDirectory>" + Dir.ToUpperInvariant())));
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public void M2_DescriptionCaseIsOrdinal()
    {
        var (r, _) = Reg(Mut(t => Rep(t, "from a.txt", "from A.txt")));
        Assert.Contains("(Description)", r.Error);
    }

    [Fact]
    public void M2_CompareOrderIdBeforeRun()
    {
        var (r, _) = Reg(Mut(t => Rep(Rep(t, "/runplan 20261125-1533-9a1f", "/runplan 20261125-1534-9a1f"), "2026-11-25T15:33:00-08:00", "2026-11-25T15:34:00-08:00")));
        Assert.Contains("(Id)", r.Error);
    }

    [Fact]
    public void M3_CreateFail()
    {
        var (r, rn) = Reg([], create: 1);
        Assert.Equal("could not schedule (schtasks exit 1)", r.Error);
        Assert.Single(rn.Calls);
    }

    [Fact]
    public void M3_QueryFail()
    {
        var (r, rn) = Reg([], query: 1);
        Assert.Equal("scheduled task does not match the plan (query exit 1); it was removed", r.Error);
        Assert.Equal(3, rn.Calls.Count);
        Assert.Equal(["/delete", "/tn", Name, "/f"], rn.Calls[2].Args);
    }

    [Fact]
    public void M3_DeleteFail()
    {
        var (r, _) = Reg(Mut(t => Rep(t, "from a.txt", "from b.txt")), del: 1);
        Assert.Equal("scheduled task does not match the plan (Description); removing it failed (schtasks exit 1)", r.Error);
    }

    [Fact]
    public void M3_ThrowOnCreate()
    {
        var calls = 0;
        var rn = new Runner((i, a) => { calls++; throw new InvalidOperationException("boom"); });
        var r = new SchtasksTaskScheduler(rn, Sys).Register(Name, XmlPath, Expected());
        Assert.Equal("could not schedule (schtasks exit -1)", r.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void M3_ThrowOnQueryAndDelete()
    {
        var rn = new Runner((i, a) => i == 0 ? new ProcessResult(0, []) : throw new IOException("x"));
        var r = new SchtasksTaskScheduler(rn, Sys).Register(Name, XmlPath, Expected());
        Assert.Equal("scheduled task does not match the plan (query exit -1); removing it failed (schtasks exit -1)", r.Error);
    }

    [Fact]
    public void M4_Exists()
    {
        foreach (var (code, want) in new[] { (0, true), (1, false) })
        {
            var rn = new Runner((i, a) => new ProcessResult(code, []));
            Assert.Equal(want, new SchtasksTaskScheduler(rn, Sys).Exists(Name));
            Assert.Equal(["/query", "/tn", Name], rn.Calls.Single().Args);
            Assert.Equal(Exe, rn.Calls.Single().Exe);
        }
    }

    [Fact]
    public void M4_DeleteOk()
    {
        var rn = new Runner((i, a) => new ProcessResult(0, []));
        Assert.True(new SchtasksTaskScheduler(rn, Sys).Delete(Name).Ok);
        Assert.Equal(["/delete", "/tn", Name, "/f"], rn.Calls.Single().Args);
    }

    [Fact]
    public void M4_DeleteGoneThenOk()
    {
        var rn = new Runner((i, a) => new ProcessResult(1, []));
        Assert.True(new SchtasksTaskScheduler(rn, Sys).Delete(Name).Ok);
        Assert.Equal(2, rn.Calls.Count);
        Assert.Equal(["/query", "/tn", Name], rn.Calls[1].Args);
    }

    [Fact]
    public void M4_DeleteStillThereError()
    {
        var rn = new Runner((i, a) => new ProcessResult(i == 0 ? 1 : 0, []));
        var r = new SchtasksTaskScheduler(rn, Sys).Delete(Name);
        Assert.False(r.Ok);
        Assert.Equal("schtasks exit 1", r.Error);
    }

    [Theory]
    [InlineData(0x1F01FF, WriteRights.GenericAll)]
    [InlineData(0x10000000, WriteRights.GenericAll)]
    [InlineData(0x301BF, WriteRights.Write | WriteRights.Append | WriteRights.Delete)]
    [InlineData(0x1200A9, WriteRights.None)]
    [InlineData(0x4, WriteRights.Append)]
    [InlineData(0x2, WriteRights.Write)]
    [InlineData(0x40, WriteRights.DeleteChild)]
    [InlineData(0x10000, WriteRights.Delete)]
    [InlineData(0x40000, WriteRights.WriteDac)]
    [InlineData(0x80000, WriteRights.WriteOwner)]
    [InlineData(0x40000000, WriteRights.GenericWrite | WriteRights.Write | WriteRights.Append)]
    [InlineData(0x1F01FF | 0x40000000, WriteRights.GenericAll)]
    [InlineData(0, WriteRights.None)]
    [InlineData(0x1, WriteRights.None)]
    public void M5_Masks(int mask, WriteRights want) => Assert.Equal(want, AclRights.FromMask(mask));

    [Fact]
    public void N1_NoSlashF_NoCallAfterFailedCreate()
    {
        var (_, ok) = Reg(TaskXml.Build(Expected()));
        Assert.DoesNotContain(ok.Calls, c => c.Args.Contains("/f") && c.Args[0] == "/create");
        Assert.Equal(2, ok.Calls.Count);
        Assert.Equal("/query", ok.Calls[1].Args[0]);
        var (_, bad) = Reg([], create: 5);
        Assert.Single(bad.Calls);
        Assert.DoesNotContain("/f", bad.Calls[0].Args);
    }
}
