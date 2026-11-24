using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public class TaskXmlTests
{
    private static PlanTask CreateSampleTask(
        string id = "20261123-2230-a1b2",
        DateTimeOffset? run = null,
        DateTimeOffset? expires = null,
        string userSid = "S-1-5-21-1234567890-1234567890-1234567890-1001",
        string planDir = @"C:\Users\test\AppData\Local\dirused\plans\20261123-2230-a1b2",
        string description = "Scheduled purge plan")
    {
        return new PlanTask(
            id,
            run ?? new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8)),
            expires ?? new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(-8)),
            userSid,
            planDir,
            description);
    }

    [Fact]
    public void Build_XM1_StructureAndEncoding()
    {
        var cases = new[]
        {
            // Case 1: Standard PST
            (Run: new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8)),
             Expires: new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(-8)),
             ExpectedStart: "2026-11-23T22:30:00-08:00",
             ExpectedEnd: "2026-11-24T21:00:00-08:00"),
            // Case 2: PDT
            (Run: new DateTimeOffset(2026, 6, 1, 22, 30, 0, TimeSpan.FromHours(-7)),
             Expires: new DateTimeOffset(2026, 6, 2, 21, 0, 0, TimeSpan.FromHours(-7)),
             ExpectedStart: "2026-06-01T22:30:00-07:00",
             ExpectedEnd: "2026-06-02T21:00:00-07:00"),
            // Case 3: UTC (+00:00)
            (Run: new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.Zero),
             Expires: new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero),
             ExpectedStart: "2026-11-23T22:30:00+00:00",
             ExpectedEnd: "2026-11-24T21:00:00+00:00")
        };

        foreach (var c in cases)
        {
            var taskWithSlash = CreateSampleTask(
                run: c.Run,
                expires: c.Expires,
                planDir: @"C:\Users\test\AppData\Local\dirused\plans\20261123-2230-a1b2\");

            byte[] bytes = TaskXml.Build(taskWithSlash);

            // bytes start FF FE
            Assert.True(bytes.Length >= 2);
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);

            // decoded text starts exactly '<?xml version="1.0" encoding="UTF-16"?>'
            string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", text);

            // contains no '\r' or '\n'
            Assert.DoesNotContain("\r", text);
            Assert.DoesNotContain("\n", text);

            // Task has namespace and version 1.2
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var doc = XDocument.Parse(text);
            var root = doc.Root;
            Assert.NotNull(root);
            Assert.Equal("Task", root.Name.LocalName);
            Assert.Equal(ns.NamespaceName, root.Name.NamespaceName);
            Assert.Equal("1.2", root.Attribute("version")?.Value);

            // Task children appear in exact Scope order: RegistrationInfo, Triggers, Principals, Settings, Actions
            var taskChildren = root.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "RegistrationInfo", "Triggers", "Principals", "Settings", "Actions" }, taskChildren);

            // RegistrationInfo children: Description, SecurityDescriptor
            var regChildren = root.Element(ns + "RegistrationInfo")!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "Description", "SecurityDescriptor" }, regChildren);

            // TimeTrigger children in Scope order: StartBoundary, EndBoundary, Enabled
            var triggerChildren = root.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "StartBoundary", "EndBoundary", "Enabled" }, triggerChildren);

            // Principal children in Scope order: UserId, LogonType, RunLevel
            var principalChildren = root.Element(ns + "Principals")!.Element(ns + "Principal")!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "UserId", "LogonType", "RunLevel" }, principalChildren);

            // Settings children in Scope order:
            // MultipleInstancesPolicy, DisallowStartIfOnBatteries, StopIfGoingOnBatteries, AllowHardTerminate,
            // StartWhenAvailable, AllowStartOnDemand, Enabled, Hidden, ExecutionTimeLimit, DeleteExpiredTaskAfter
            var settingsChildren = root.Element(ns + "Settings")!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[]
            {
                "MultipleInstancesPolicy",
                "DisallowStartIfOnBatteries",
                "StopIfGoingOnBatteries",
                "AllowHardTerminate",
                "StartWhenAvailable",
                "AllowStartOnDemand",
                "Enabled",
                "Hidden",
                "ExecutionTimeLimit",
                "DeleteExpiredTaskAfter"
            }, settingsChildren);

            // Exec children in Scope order: Command, Arguments, WorkingDirectory
            var execChildren = root.Element(ns + "Actions")!.Element(ns + "Exec")!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "Command", "Arguments", "WorkingDirectory" }, execChildren);

            // StartBoundary / EndBoundary text
            Assert.Equal(c.ExpectedStart, root.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "StartBoundary")!.Value);
            Assert.Equal(c.ExpectedEnd, root.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "EndBoundary")!.Value);

            // Scope values
            Assert.Equal("D:P(A;;FA;;;S-1-5-21-1234567890-1234567890-1234567890-1001)(A;;FA;;;SY)(A;;FA;;;BA)",
                root.Element(ns + "RegistrationInfo")!.Element(ns + "SecurityDescriptor")!.Value);
            Assert.Equal("true", root.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "Enabled")!.Value);
            Assert.Equal("Author", root.Element(ns + "Principals")!.Element(ns + "Principal")!.Attribute("id")!.Value);
            Assert.Equal("S-1-5-21-1234567890-1234567890-1234567890-1001", root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "UserId")!.Value);
            Assert.Equal("InteractiveToken", root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "LogonType")!.Value);
            Assert.Equal("LeastPrivilege", root.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value);

            var s = root.Element(ns + "Settings")!;
            Assert.Equal("IgnoreNew", s.Element(ns + "MultipleInstancesPolicy")!.Value);
            Assert.Equal("false", s.Element(ns + "DisallowStartIfOnBatteries")!.Value);
            Assert.Equal("false", s.Element(ns + "StopIfGoingOnBatteries")!.Value);
            Assert.Equal("true", s.Element(ns + "AllowHardTerminate")!.Value);
            Assert.Equal("false", s.Element(ns + "StartWhenAvailable")!.Value);
            Assert.Equal("false", s.Element(ns + "AllowStartOnDemand")!.Value);
            Assert.Equal("true", s.Element(ns + "Enabled")!.Value);
            Assert.Equal("false", s.Element(ns + "Hidden")!.Value);
            Assert.Equal("PT2H", s.Element(ns + "ExecutionTimeLimit")!.Value);
            Assert.Equal("P30D", s.Element(ns + "DeleteExpiredTaskAfter")!.Value);

            var actions = root.Element(ns + "Actions")!;
            Assert.Equal("Author", actions.Attribute("Context")!.Value);
            var exec = actions.Element(ns + "Exec")!;
            Assert.Equal(@"C:\Users\test\AppData\Local\dirused\plans\20261123-2230-a1b2\dirused.exe", exec.Element(ns + "Command")!.Value);
            Assert.Equal("/runplan 20261123-2230-a1b2", exec.Element(ns + "Arguments")!.Value);
            Assert.Equal(@"C:\Users\test\AppData\Local\dirused\plans\20261123-2230-a1b2\", exec.Element(ns + "WorkingDirectory")!.Value);
        }
    }

    [Fact]
    public void RoundTrip_XM2_VariousInputsAndByteIdentical()
    {
        var tasks = new[]
        {
            CreateSampleTask(run: new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.FromHours(-8)), expires: new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(-8))),
            CreateSampleTask(run: new DateTimeOffset(2026, 6, 1, 22, 30, 0, TimeSpan.FromHours(-7)), expires: new DateTimeOffset(2026, 6, 2, 21, 0, 0, TimeSpan.FromHours(-7))),
            CreateSampleTask(run: new DateTimeOffset(2026, 11, 23, 22, 30, 0, TimeSpan.Zero), expires: new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero)),
            CreateSampleTask(planDir: @"C:\Users\o p\AppData\Local\dirused\plans\20261123-2230-a1b2 & ""x"" <y> 'z'"),
            CreateSampleTask(description: ""),
            CreateSampleTask(description: "&<>")
        };

        foreach (var t in tasks)
        {
            byte[] bytes1 = TaskXml.Build(t);
            byte[] bytes2 = TaskXml.Build(t);
            Assert.Equal(bytes1, bytes2); // byte-identical across two calls

            var parsed = TaskXml.Parse(bytes1);
            Assert.Equal(t, parsed);
            Assert.Equal(t.Run.Offset, parsed.Run.Offset);
            Assert.Equal(t.Expires.Offset, parsed.Expires.Offset);
        }
    }

    [Fact]
    public void Parse_XM3_AlternativeEncodingsAndReadbackForms()
    {
        var task = CreateSampleTask();
        byte[] built = TaskXml.Build(task);

        // SecurityDescriptorOf returns built SDDL
        Assert.Equal($"D:P(A;;FA;;;{task.UserSid})(A;;FA;;;SY)(A;;FA;;;BA)", TaskXml.SecurityDescriptorOf(built));

        // BoundariesOf returns the two boundary strings
        var (start, end) = TaskXml.BoundariesOf(built);
        Assert.Equal(task.Run.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), start);
        Assert.Equal(task.Expires.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), end);

        // UTF-16 LE without BOM
        byte[] utf16NoBom = built.Skip(2).ToArray();
        Assert.Equal(task, TaskXml.Parse(utf16NoBom));

        // UTF-8 without BOM
        string xmlText = Encoding.Unicode.GetString(built, 2, built.Length - 2);
        byte[] utf8NoBom = Encoding.UTF8.GetBytes(xmlText);
        Assert.Equal(task, TaskXml.Parse(utf8NoBom));

        // UTF-8 with BOM
        byte[] utf8WithBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8NoBom).ToArray();
        Assert.Equal(task, TaskXml.Parse(utf8WithBom));

        // Version 1.3
        string v13Xml = xmlText.Replace("version=\"1.2\"", "version=\"1.3\"");
        Assert.Equal(task, TaskXml.Parse(Encoding.UTF8.GetBytes(v13Xml)));

        // Extra RegistrationInfo/URI, Author, Date, Settings/IdleSettings, Settings/Priority
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = XDocument.Parse(xmlText);
        var reg = doc.Root!.Element(ns + "RegistrationInfo")!;
        reg.Add(new XElement(ns + "URI", @"\dirused\task1"));
        reg.Add(new XElement(ns + "Author", "TestAuthor"));
        reg.Add(new XElement(ns + "Date", "2026-11-23T22:30:00"));

        var settings = doc.Root!.Element(ns + "Settings")!;
        settings.Add(new XElement(ns + "IdleSettings"));
        settings.Add(new XElement(ns + "Priority", "7"));

        byte[] withExtra = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(task, TaskXml.Parse(withExtra));

        // Document with children in another order
        var reversedChildrenDoc = new XDocument(
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                doc.Root!.Element(ns + "Actions")!,
                doc.Root!.Element(ns + "Settings")!,
                doc.Root!.Element(ns + "Principals")!,
                doc.Root!.Element(ns + "Triggers")!,
                doc.Root!.Element(ns + "RegistrationInfo")!));
        byte[] reversedOrderBytes = Encoding.UTF8.GetBytes(reversedChildrenDoc.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(task, TaskXml.Parse(reversedOrderBytes));

        // Readback form: StartWhenAvailable, Hidden, both Enabled, RunLevel, AllowHardTerminate, MultipleInstancesPolicy removed
        var readbackDoc = XDocument.Parse(xmlText);
        var rSettings = readbackDoc.Root!.Element(ns + "Settings")!;
        rSettings.Element(ns + "StartWhenAvailable")?.Remove();
        rSettings.Element(ns + "Hidden")?.Remove();
        rSettings.Element(ns + "Enabled")?.Remove();
        rSettings.Element(ns + "AllowHardTerminate")?.Remove();
        rSettings.Element(ns + "MultipleInstancesPolicy")?.Remove();

        var rPrincipal = readbackDoc.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!;
        rPrincipal.Element(ns + "RunLevel")?.Remove();

        var rTrigger = readbackDoc.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!;
        rTrigger.Element(ns + "Enabled")?.Remove();

        byte[] readbackBytes = Encoding.UTF8.GetBytes(readbackDoc.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(task, TaskXml.Parse(readbackBytes));

        // SecurityDescriptorOf and BoundariesOf return null for missing elements
        var minimalDoc = new XElement(ns + "Task", new XAttribute("version", "1.2"));
        byte[] minimalBytes = Encoding.UTF8.GetBytes(minimalDoc.ToString(SaveOptions.DisableFormatting));
        Assert.Null(TaskXml.SecurityDescriptorOf(minimalBytes));
        var (emptyStart, emptyEnd) = TaskXml.BoundariesOf(minimalBytes);
        Assert.Null(emptyStart);
        Assert.Null(emptyEnd);
    }

    [Fact]
    public void Parse_XM4_MutationsThrowFormatExceptionWithExpectedPrefix()
    {
        var task = CreateSampleTask();
        string baseXml = Encoding.Unicode.GetString(TaskXml.Build(task)).TrimStart('\uFEFF');
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        void AssertMutationThrows(Action<XDocument> mutate, string expectedPrefix)
        {
            var doc = XDocument.Parse(baseXml);
            mutate(doc);
            byte[] bytes = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
            var ex = Assert.Throws<FormatException>(() => TaskXml.Parse(bytes));
            Assert.StartsWith(expectedPrefix + ": ", ex.Message);
        }

        void AssertXmlMutationThrows(Func<string, string> mutate, string expectedPrefix)
        {
            string mutatedXml = mutate(baseXml);
            byte[] bytes = Encoding.UTF8.GetBytes(mutatedXml);
            var ex = Assert.Throws<FormatException>(() => TaskXml.Parse(bytes));
            Assert.StartsWith(expectedPrefix + ": ", ex.Message);
        }

        // AllowStartOnDemand true or removed [AllowStartOnDemand]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "AllowStartOnDemand")!.Value = "true", "AllowStartOnDemand");
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "AllowStartOnDemand")!.Remove(), "AllowStartOnDemand");

        // StartWhenAvailable true [StartWhenAvailable]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "StartWhenAvailable")!.Value = "true", "StartWhenAvailable");

        // LogonType Password [LogonType]
        AssertMutationThrows(d => d.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "LogonType")!.Value = "Password", "LogonType");

        // RunLevel HighestAvailable [RunLevel]
        AssertMutationThrows(d => d.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "RunLevel")!.Value = "HighestAvailable", "RunLevel");

        // MultipleInstancesPolicy Parallel [MultipleInstancesPolicy]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "MultipleInstancesPolicy")!.Value = "Parallel", "MultipleInstancesPolicy");

        // ExecutionTimeLimit PT72H [ExecutionTimeLimit]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value = "PT72H", "ExecutionTimeLimit");

        // DeleteExpiredTaskAfter removed [DeleteExpiredTaskAfter]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "DeleteExpiredTaskAfter")!.Remove(), "DeleteExpiredTaskAfter");

        // DisallowStartIfOnBatteries removed [DisallowStartIfOnBatteries]
        AssertMutationThrows(d => d.Root!.Element(ns + "Settings")!.Element(ns + "DisallowStartIfOnBatteries")!.Remove(), "DisallowStartIfOnBatteries");

        // a second TimeTrigger [Triggers]
        AssertMutationThrows(d =>
        {
            var triggers = d.Root!.Element(ns + "Triggers")!;
            triggers.Add(new XElement(triggers.Element(ns + "TimeTrigger")!));
        }, "Triggers");

        // StartBoundary without offset and with 'Z' [StartBoundary]
        AssertMutationThrows(d => d.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "StartBoundary")!.Value = "2026-11-23T22:30:00", "StartBoundary");
        AssertMutationThrows(d => d.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "StartBoundary")!.Value = "2026-11-23T22:30:00Z", "StartBoundary");

        // EndBoundary removed [EndBoundary]
        AssertMutationThrows(d => d.Root!.Element(ns + "Triggers")!.Element(ns + "TimeTrigger")!.Element(ns + "EndBoundary")!.Remove(), "EndBoundary");

        // Command in another folder [Command]
        AssertMutationThrows(d => d.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value = @"C:\other\dirused.exe", "Command");

        // Arguments '/runplan bad' and with trailing newline [Arguments]
        AssertMutationThrows(d => d.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")!.Value = "/runplan bad", "Arguments");
        AssertXmlMutationThrows(xml => xml.Replace("/runplan 20261123-2230-a1b2", "/runplan 20261123-2230-a1b2&#xA;"), "Arguments");

        // UserId 'Administrator' [UserId]
        AssertMutationThrows(d => d.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "UserId")!.Value = "Administrator", "UserId");

        // Context 'Other' [Context]
        AssertMutationThrows(d => d.Root!.Element(ns + "Actions")!.Attribute("Context")!.Value = "Other", "Context");

        // wrong namespace [Task]
        AssertXmlMutationThrows(xml => xml.Replace("http://schemas.microsoft.com/windows/2004/02/mit/task", "http://schemas.microsoft.com/wrong"), "Task");

        // version 1.1 [version]
        AssertMutationThrows(d => d.Root!.Attribute("version")!.Value = "1.1", "version");

        // A document with an extra ACE in SecurityDescriptor still parses
        var docWithAce = XDocument.Parse(baseXml);
        docWithAce.Root!.Element(ns + "RegistrationInfo")!.Element(ns + "SecurityDescriptor")!.Value += "(A;;FA;;;WD)";
        byte[] bytesWithAce = Encoding.UTF8.GetBytes(docWithAce.ToString(SaveOptions.DisableFormatting));
        var parsed = TaskXml.Parse(bytesWithAce);
        Assert.Equal(task, parsed);
    }

    [Fact]
    public void Build_XM5_InputValidation()
    {
        // Id 'bad' or with trailing newline
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(id: "bad")));
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(id: "20261123-2230-a1b2\n")));

        // UserSid 'Administrator' or with trailing newline
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(userSid: "Administrator")));
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(userSid: "S-1-5-21-1\n")));

        // empty PlanDir
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(planDir: "")));

        // Run with 500 ms
        var run500ms = new DateTimeOffset(2026, 11, 23, 22, 30, 0, 500, TimeSpan.FromHours(-8));
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(run: run500ms)));

        // Description with CR or LF
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(description: "a\nb")));
        Assert.Throws<ArgumentException>(() => TaskXml.Build(CreateSampleTask(description: "a\rb")));
    }

    [Fact]
    public void RoundTrip_XN1_XmlEntitiesSpecialCharacters()
    {
        var taskWithEntities = CreateSampleTask(
            planDir: @"C:\dir with spaces & ""quotes"" <angle> 'single'",
            description: "text with & < > \" ' special characters");

        byte[] bytes = TaskXml.Build(taskWithEntities);
        var parsed = TaskXml.Parse(bytes);
        Assert.Equal(taskWithEntities, parsed);
    }

    [Fact]
    public void Boundaries_XN2_NeverWrittenWithoutOffsetOrWithZ()
    {
        var task = CreateSampleTask();
        byte[] bytes = TaskXml.Build(task);
        var (start, end) = TaskXml.BoundariesOf(bytes);

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.False(start!.EndsWith("Z"));
        Assert.False(end!.EndsWith("Z"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", start);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", end);
    }

    [Fact]
    public void SourceScan_XN3_NoForbiddenReferences()
    {
        // Walk up from AppContext.BaseDirectory to the directory holding dirused.sln, then src/Dirused.Common/TaskXml.cs
        string? current = AppContext.BaseDirectory;
        string? slnDir = null;
        while (!string.IsNullOrEmpty(current))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(current, "dirused.sln")))
            {
                slnDir = current;
                break;
            }
            current = System.IO.Directory.GetParent(current)?.FullName;
        }

        Assert.NotNull(slnDir);
        string sourcePath = System.IO.Path.Combine(slnDir!, "src", "Dirused.Common", "TaskXml.cs");
        Assert.True(System.IO.File.Exists(sourcePath));

        string source = System.IO.File.ReadAllText(sourcePath);
        // Strip // comments before matching
        string stripped = Regex.Replace(source, @"//.*$", "", RegexOptions.Multiline);

        var forbiddenRegex = new Regex(@"\b(File|Directory|Process|Registry|Console)\s*\.", RegexOptions.Compiled);
        Assert.DoesNotMatch(forbiddenRegex, stripped);

        Assert.DoesNotContain("schtasks", stripped, StringComparison.OrdinalIgnoreCase);

        // Positive control: mutant adding 'File.WriteAllBytes' turns the scan red
        string mutant = stripped + "\nFile.WriteAllBytes(\"a\", new byte[0]);";
        Assert.Matches(forbiddenRegex, mutant);
    }

    [Fact]
    public void Parse_XN4_DoctypeAndEntitiesThrowFormatException()
    {
        var task = CreateSampleTask();
        string validXml = Encoding.Unicode.GetString(TaskXml.Build(task)).TrimStart('\uFEFF');
        string decl = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>";
        string body = validXml.Substring(decl.Length);

        // Document with internal DTD subset and entity reference in Description
        string dtd = "<!DOCTYPE Task [<!ENTITY e \"x\">]>";
        string bodyWithEntity = Regex.Replace(body, @"<Description[^>]*>.*?</Description>|<Description\s*/>", "<Description>&e;</Description>");
        string internalDtdXml = decl + dtd + bodyWithEntity;
        Assert.Throws<FormatException>(() => TaskXml.Parse(Encoding.UTF8.GetBytes(internalDtdXml)));

        // Positive control: the same document with entity resolved directly (no DOCTYPE) parses
        string plainXml = decl + bodyWithEntity.Replace("&e;", "x");
        var parsed = TaskXml.Parse(Encoding.UTF8.GetBytes(plainXml));
        Assert.Equal("x", parsed.Description);
    }
}
