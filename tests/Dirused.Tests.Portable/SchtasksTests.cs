using System.Text;
using System.Xml.Linq;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public class SchtasksTests
{
    private const string Name = @"\dirused\S-1-5-21-1-2-3-1001\purge-20261123-2230-a1b2";
    private const string XmlPath = @"C:\plans\task.xml";
    private static readonly PlanTask Expected = new("20261123-2230-a1b2",
        new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8)),
        new DateTimeOffset(2026, 11, 24, 22, 30, 0, TimeSpan.FromHours(-8)),
        "S-1-5-21-1-2-3-1001", @"C:\plans\20261123-2230-a1b2", "scheduled purge");

    [Fact]
    public void RegisterUsesExpectedArgvAndAcceptsBuiltXmlAndInstantEquivalent()
    {
        var runner = new RecordingRunner((_, _) => new(0, []), (_, _) => new(0, TaskXml.Build(Expected)));
        var scheduler = New(runner);
        Assert.True(scheduler.Register(Name, XmlPath, Expected).Ok);
        Assert.Equal(2, runner.Calls.Count);
        AssertCall(runner.Calls[0], ["/create", "/tn", Name, "/xml", XmlPath]);
        AssertCall(runner.Calls[1], ["/query", "/tn", Name, "/xml"]);

        var equivalent = Encoding.Unicode.GetString(TaskXml.Build(Expected), 2, TaskXml.Build(Expected).Length - 2)
            .Replace("2026-11-23T22:30:00-08:00", "2026-11-24T06:30:00+00:00")
            .Replace("2026-11-24T22:30:00-08:00", "2026-11-25T06:30:00+00:00");
        var bytes = Encoding.UTF8.GetBytes(equivalent);
        runner = new RecordingRunner((_, _) => new(0, []), (_, _) => new(0, bytes));
        Assert.True(New(runner).Register(Name, XmlPath, Expected).Ok);
        Assert.Equal(2, runner.Calls.Count);

        var p9 = XDocument.Parse(Encoding.Unicode.GetString(TaskXml.Build(Expected), 2, TaskXml.Build(Expected).Length - 2));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        p9.Root!.Element(ns + "RegistrationInfo")!.Add(new XElement(ns + "URI", Name));
        p9.Root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Remove();
        var p9Settings = p9.Root.Element(ns + "Settings")!;
        foreach (var optional in new[] { "MultipleInstancesPolicy", "AllowHardTerminate", "StartWhenAvailable", "Enabled", "Hidden" })
            p9Settings.Element(ns + optional)?.Remove();
        p9Settings.Add(new XElement(ns + "IdleSettings", new XElement(ns + "StopOnIdleEnd", "true")));
        runner = new RecordingRunner((_, _) => new(0, []), (_, _) => new(0, Encoding.UTF8.GetBytes(p9.ToString(SaveOptions.DisableFormatting))));
        Assert.True(New(runner).Register(Name, XmlPath, Expected).Ok);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Run")]
    [InlineData("Expires")]
    [InlineData("UserSid")]
    [InlineData("PlanDir")]
    [InlineData("Description")]
    [InlineData("SecurityDescriptor")]
    [InlineData("AllowStartOnDemand")]
    [InlineData("Arguments")]
    [InlineData("Command")]
    public void MismatchDeletesAndReportsFirstField(string field)
    {
        var bytes = Mutate(field);
        var runner = new RecordingRunner((_, _) => new(0, []), (_, _) => new(0, bytes), (_, _) => new(0, []));
        var result = New(runner).Register(Name, XmlPath, Expected);
        Assert.False(result.Ok);
        Assert.Equal($"scheduled task does not match the plan ({field}); it was removed", result.Error);
        Assert.Equal(3, runner.Calls.Count);
        AssertCall(runner.Calls[2], ["/delete", "/tn", Name, "/f"]);
    }

    [Fact]
    public void RegisterFailurePathsAreReported()
    {
        var createFail = new RecordingRunner((_, _) => new(1, []));
        Assert.Equal("could not schedule (schtasks exit 1)", New(createFail).Register(Name, XmlPath, Expected).Error);
        Assert.Single(createFail.Calls);

        var queryFail = new RecordingRunner((_, _) => new(0, []), (_, _) => new(1, []), (_, _) => new(0, []));
        Assert.Equal("scheduled task does not match the plan (query exit 1); it was removed", New(queryFail).Register(Name, XmlPath, Expected).Error);

        var deleteFail = new RecordingRunner((_, _) => new(0, []), (_, _) => new(1, []), (_, _) => new(1, []));
        Assert.Equal("scheduled task does not match the plan (query exit 1); removing it failed (schtasks exit 1)", New(deleteFail).Register(Name, XmlPath, Expected).Error);

        var throws = new RecordingRunner((_, _) => throw new InvalidOperationException());
        Assert.Equal("could not schedule (schtasks exit -1)", New(throws).Register(Name, XmlPath, Expected).Error);
    }

    [Fact]
    public void ExistsAndDeleteUseExitCodesAndTreatAlreadyGoneAsSuccess()
    {
        var exists = new RecordingRunner((_, _) => new(0, []));
        Assert.True(New(exists).Exists(Name));
        AssertCall(exists.Calls[0], ["/query", "/tn", Name]);
        exists = new RecordingRunner((_, _) => new(1, []));
        Assert.False(New(exists).Exists(Name));

        Assert.True(New(new RecordingRunner((_, _) => new(0, []))).Delete(Name).Ok);
        var gone = new RecordingRunner((_, _) => new(1, []), (_, _) => new(1, []));
        Assert.True(New(gone).Delete(Name).Ok);
        var present = new RecordingRunner((_, _) => new(1, []), (_, _) => new(0, []));
        Assert.Equal("schtasks exit 1", New(present).Delete(Name).Error);
        AssertCall(gone.Calls[0], ["/delete", "/tn", Name, "/f"]);
        AssertCall(gone.Calls[1], ["/query", "/tn", Name]);
    }

    private static SchtasksTaskScheduler New(RecordingRunner runner) => new(runner, @"C:\Windows\System32\");

    private static void AssertCall((string Exe, string[] Args) call, string[] args)
    {
        Assert.Equal(@"C:\Windows\System32\schtasks.exe", call.Exe);
        Assert.Equal(args, call.Args);
    }

    private static byte[] Mutate(string field)
    {
        var text = Encoding.Unicode.GetString(TaskXml.Build(Expected), 2, TaskXml.Build(Expected).Length - 2);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = XDocument.Parse(text);
        switch (field)
        {
            case "Id": doc.Descendants(ns + "Arguments").Single().Value = "/runplan 20261124-2230-a1b2"; break;
            case "Run": doc.Descendants(ns + "StartBoundary").Single().Value = "2026-11-23T23:30:00-08:00"; break;
            case "Expires": doc.Descendants(ns + "EndBoundary").Single().Value = "2026-11-24T23:30:00-08:00"; break;
            case "UserSid": doc.Descendants(ns + "UserId").Single().Value = "S-1-5-21-2-3-4-1002"; break;
            case "PlanDir": doc.Descendants(ns + "WorkingDirectory").Single().Value = @"C:\other"; doc.Descendants(ns + "Command").Single().Value = @"C:\other\dirused.exe"; break;
            case "Description": doc.Descendants(ns + "Description").Single().Value = "different"; break;
            case "SecurityDescriptor": doc.Descendants(ns + "SecurityDescriptor").Single().Value = "D:P(A;;FA;;;SY)"; break;
            case "AllowStartOnDemand": doc.Descendants(ns + "AllowStartOnDemand").Single().Value = "true"; break;
            case "Arguments": doc.Descendants(ns + "Arguments").Single().Value = "other"; break;
            case "Command": doc.Descendants(ns + "Command").Single().Value = @"C:\other\dirused.exe"; break;
        }
        return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private sealed class RecordingRunner(params Func<string, IReadOnlyList<string>, ProcessResult>[] responses) : IProcessRunner
    {
        private readonly Queue<Func<string, IReadOnlyList<string>, ProcessResult>> _responses = new(responses);
        public List<(string Exe, string[] Args)> Calls { get; } = [];
        public ProcessResult Run(string exe, IReadOnlyList<string> args)
        {
            Calls.Add((exe, args.ToArray()));
            return _responses.Dequeue()(exe, args);
        }
    }
}
