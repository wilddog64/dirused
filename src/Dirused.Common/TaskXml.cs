using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Dirused.Common;

public sealed record PlanTask(
    string Id,
    DateTimeOffset Run,
    DateTimeOffset Expires,
    string UserSid,
    string PlanDir,
    string Description);

public static class TaskXml
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly Regex IdRegex = new(@"\A[0-9]{8}-[0-9]{4}-[0-9a-f]{4}\z", RegexOptions.Compiled);
    private static readonly Regex UserSidRegex = new(@"\AS-1-[0-9]+(-[0-9]+)+\z", RegexOptions.Compiled);
    private static readonly Regex BoundaryRegex = new(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d\z", RegexOptions.Compiled);
    private static readonly Regex ArgumentsRegex = new(@"\A/runplan ([0-9]{8}-[0-9]{4}-[0-9a-f]{4})\z", RegexOptions.Compiled);

    public static byte[] Build(PlanTask task)
    {
        if (task == null) throw new ArgumentNullException(nameof(task));
        if (string.IsNullOrEmpty(task.Id) || !IdRegex.IsMatch(task.Id))
            throw new ArgumentException("Id must match ^[0-9]{8}-[0-9]{4}-[0-9a-f]{4}$", nameof(task));
        if (string.IsNullOrEmpty(task.UserSid) || !UserSidRegex.IsMatch(task.UserSid))
            throw new ArgumentException("UserSid must match ^S-1-[0-9]+(-[0-9]+)+$", nameof(task));
        if (string.IsNullOrEmpty(task.PlanDir))
            throw new ArgumentException("PlanDir must not be empty", nameof(task));
        if (task.Run.Ticks % TimeSpan.TicksPerSecond != 0 || task.Expires.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Run and Expires must not have sub-second parts", nameof(task));
        if (task.Description != null && (task.Description.Contains('\r') || task.Description.Contains('\n')))
            throw new ArgumentException("Description must not contain CR or LF", nameof(task));

        var encoding = new UnicodeEncoding(false, true);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xFE }, 0, 2);

        var settings = new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = true,
            Indent = false
        };

        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteRaw("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            writer.WriteStartElement("Task", TaskNamespace);
            writer.WriteAttributeString("version", "1.2");

            writer.WriteStartElement("RegistrationInfo");
            writer.WriteElementString("Description", task.Description ?? "");
            writer.WriteElementString("SecurityDescriptor", $"D:P(A;;FA;;;{task.UserSid})(A;;FA;;;SY)(A;;FA;;;BA)");
            writer.WriteEndElement();

            writer.WriteStartElement("Triggers");
            writer.WriteStartElement("TimeTrigger");
            writer.WriteElementString("StartBoundary", task.Run.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            writer.WriteElementString("EndBoundary", task.Expires.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            writer.WriteElementString("Enabled", "true");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("Principals");
            writer.WriteStartElement("Principal");
            writer.WriteAttributeString("id", "Author");
            writer.WriteElementString("UserId", task.UserSid);
            writer.WriteElementString("LogonType", "InteractiveToken");
            writer.WriteElementString("RunLevel", "LeastPrivilege");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("Settings");
            writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew");
            writer.WriteElementString("DisallowStartIfOnBatteries", "false");
            writer.WriteElementString("StopIfGoingOnBatteries", "false");
            writer.WriteElementString("AllowHardTerminate", "true");
            writer.WriteElementString("StartWhenAvailable", "false");
            writer.WriteElementString("AllowStartOnDemand", "false");
            writer.WriteElementString("Enabled", "true");
            writer.WriteElementString("Hidden", "false");
            writer.WriteElementString("ExecutionTimeLimit", "PT2H");
            writer.WriteElementString("DeleteExpiredTaskAfter", "P30D");
            writer.WriteEndElement();

            writer.WriteStartElement("Actions");
            writer.WriteAttributeString("Context", "Author");
            writer.WriteStartElement("Exec");
            string command = task.PlanDir.TrimEnd('\\') + "\\dirused.exe";
            writer.WriteElementString("Command", command);
            writer.WriteElementString("Arguments", $"/runplan {task.Id}");
            writer.WriteElementString("WorkingDirectory", task.PlanDir);
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement();
            writer.Flush();
        }

        return ms.ToArray();
    }

    public static PlanTask Parse(byte[] bytes)
    {
        var doc = LoadDocument(bytes);
        XElement root = doc.Root ?? throw new FormatException("Task: missing root element");

        if (root.Name.LocalName != "Task")
        {
            throw new FormatException($"Task: expected root element Task, got {root.Name.LocalName}");
        }
        if (root.Name.NamespaceName != TaskNamespace)
        {
            throw new FormatException($"Task: expected namespace {TaskNamespace}, got {root.Name.NamespaceName}");
        }

        var versionAttr = root.Attribute("version");
        if (versionAttr == null || (versionAttr.Value != "1.2" && versionAttr.Value != "1.3" && versionAttr.Value != "1.4"))
        {
            throw new FormatException($"version: expected 1.2, 1.3 or 1.4, got {versionAttr?.Value}");
        }

        XNamespace ns = TaskNamespace;

        var regInfo = root.Element(ns + "RegistrationInfo");
        string description = regInfo?.Element(ns + "Description")?.Value ?? "";

        var triggers = root.Element(ns + "Triggers");
        if (triggers == null)
        {
            throw new FormatException("Triggers: missing Triggers element");
        }
        var triggerElements = triggers.Elements().ToList();
        if (triggerElements.Count != 1 || triggerElements[0].Name != ns + "TimeTrigger")
        {
            throw new FormatException("Triggers: expected exactly one TimeTrigger");
        }
        var timeTrigger = triggerElements[0];

        var startBoundaryElem = timeTrigger.Element(ns + "StartBoundary");
        if (startBoundaryElem == null || !BoundaryRegex.IsMatch(startBoundaryElem.Value))
        {
            throw new FormatException($"StartBoundary: invalid or missing boundary '{startBoundaryElem?.Value}'");
        }
        if (!DateTimeOffset.TryParseExact(startBoundaryElem.Value, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var run))
        {
            throw new FormatException($"StartBoundary: unable to parse '{startBoundaryElem.Value}'");
        }

        var endBoundaryElem = timeTrigger.Element(ns + "EndBoundary");
        if (endBoundaryElem == null || !BoundaryRegex.IsMatch(endBoundaryElem.Value))
        {
            throw new FormatException($"EndBoundary: invalid or missing boundary '{endBoundaryElem?.Value}'");
        }
        if (!DateTimeOffset.TryParseExact(endBoundaryElem.Value, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
        {
            throw new FormatException($"EndBoundary: unable to parse '{endBoundaryElem.Value}'");
        }

        var triggerEnabledElem = timeTrigger.Element(ns + "Enabled");
        if (triggerEnabledElem != null && triggerEnabledElem.Value != "true")
        {
            throw new FormatException($"Enabled: expected true, got {triggerEnabledElem.Value}");
        }

        var principals = root.Element(ns + "Principals");
        if (principals == null)
        {
            throw new FormatException("Principals: missing Principals element");
        }
        var principal = principals.Element(ns + "Principal");
        if (principal == null)
        {
            throw new FormatException("Principal: missing Principal element");
        }
        string? principalId = principal.Attribute("id")?.Value;
        if (string.IsNullOrEmpty(principalId))
        {
            throw new FormatException("Principal: missing or empty id attribute");
        }

        var userIdElem = principal.Element(ns + "UserId");
        if (userIdElem == null || !UserSidRegex.IsMatch(userIdElem.Value))
        {
            throw new FormatException($"UserId: invalid or missing SID '{userIdElem?.Value}'");
        }
        string userSid = userIdElem.Value;

        var logonTypeElem = principal.Element(ns + "LogonType");
        if (logonTypeElem == null || logonTypeElem.Value != "InteractiveToken")
        {
            throw new FormatException($"LogonType: expected InteractiveToken, got '{logonTypeElem?.Value}'");
        }

        var runLevelElem = principal.Element(ns + "RunLevel");
        if (runLevelElem != null && runLevelElem.Value != "LeastPrivilege")
        {
            throw new FormatException($"RunLevel: expected LeastPrivilege, got '{runLevelElem.Value}'");
        }

        var settings = root.Element(ns + "Settings");
        if (settings == null)
        {
            throw new FormatException("Settings: missing Settings element");
        }

        var mipElem = settings.Element(ns + "MultipleInstancesPolicy");
        if (mipElem != null && mipElem.Value != "IgnoreNew")
        {
            throw new FormatException($"MultipleInstancesPolicy: expected IgnoreNew, got '{mipElem.Value}'");
        }

        var dsibElem = settings.Element(ns + "DisallowStartIfOnBatteries");
        if (dsibElem == null || dsibElem.Value != "false")
        {
            throw new FormatException($"DisallowStartIfOnBatteries: expected false, got '{dsibElem?.Value}'");
        }

        var sigbElem = settings.Element(ns + "StopIfGoingOnBatteries");
        if (sigbElem == null || sigbElem.Value != "false")
        {
            throw new FormatException($"StopIfGoingOnBatteries: expected false, got '{sigbElem?.Value}'");
        }

        var ahtElem = settings.Element(ns + "AllowHardTerminate");
        if (ahtElem != null && ahtElem.Value != "true")
        {
            throw new FormatException($"AllowHardTerminate: expected true, got '{ahtElem.Value}'");
        }

        var swaElem = settings.Element(ns + "StartWhenAvailable");
        if (swaElem != null && swaElem.Value != "false")
        {
            throw new FormatException($"StartWhenAvailable: expected false, got '{swaElem.Value}'");
        }

        var asodElem = settings.Element(ns + "AllowStartOnDemand");
        if (asodElem == null || asodElem.Value != "false")
        {
            throw new FormatException($"AllowStartOnDemand: expected false, got '{asodElem?.Value}'");
        }

        var enabledElem = settings.Element(ns + "Enabled");
        if (enabledElem != null && enabledElem.Value != "true")
        {
            throw new FormatException($"Enabled: expected true, got '{enabledElem.Value}'");
        }

        var hiddenElem = settings.Element(ns + "Hidden");
        if (hiddenElem != null && hiddenElem.Value != "false")
        {
            throw new FormatException($"Hidden: expected false, got '{hiddenElem.Value}'");
        }

        var etlElem = settings.Element(ns + "ExecutionTimeLimit");
        if (etlElem == null || etlElem.Value != "PT2H")
        {
            throw new FormatException($"ExecutionTimeLimit: expected PT2H, got '{etlElem?.Value}'");
        }

        var detaElem = settings.Element(ns + "DeleteExpiredTaskAfter");
        if (detaElem == null || detaElem.Value != "P30D")
        {
            throw new FormatException($"DeleteExpiredTaskAfter: expected P30D, got '{detaElem?.Value}'");
        }

        var actions = root.Element(ns + "Actions");
        if (actions == null)
        {
            throw new FormatException("Actions: missing Actions element");
        }

        var contextAttr = actions.Attribute("Context");
        if (contextAttr == null || contextAttr.Value != principalId)
        {
            throw new FormatException($"Context: expected '{principalId}', got '{contextAttr?.Value}'");
        }

        var exec = actions.Element(ns + "Exec");
        if (exec == null)
        {
            throw new FormatException("Exec: missing Exec element");
        }

        var wdElem = exec.Element(ns + "WorkingDirectory");
        if (wdElem == null || string.IsNullOrEmpty(wdElem.Value))
        {
            throw new FormatException("WorkingDirectory: missing WorkingDirectory element");
        }
        string planDir = wdElem.Value;

        var cmdElem = exec.Element(ns + "Command");
        string expectedCommand = planDir.TrimEnd('\\') + "\\dirused.exe";
        if (cmdElem == null || !string.Equals(cmdElem.Value, expectedCommand, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Command: expected '{expectedCommand}', got '{cmdElem?.Value}'");
        }

        var argsElem = exec.Element(ns + "Arguments");
        if (argsElem == null)
        {
            throw new FormatException("Arguments: missing Arguments element");
        }
        var match = ArgumentsRegex.Match(argsElem.Value);
        if (!match.Success)
        {
            throw new FormatException($"Arguments: expected '/runplan <id>', got '{argsElem.Value}'");
        }
        string id = match.Groups[1].Value;

        return new PlanTask(id, run, expires, userSid, planDir, description);
    }

    public static string? SecurityDescriptorOf(byte[] bytes)
    {
        var doc = LoadDocument(bytes);
        return doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "RegistrationInfo")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "SecurityDescriptor")?.Value;
    }

    public static (string? Start, string? End) BoundariesOf(byte[] bytes)
    {
        var doc = LoadDocument(bytes);
        var timeTrigger = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Triggers")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeTrigger");
        string? start = timeTrigger?.Elements().FirstOrDefault(e => e.Name.LocalName == "StartBoundary")?.Value;
        string? end = timeTrigger?.Elements().FirstOrDefault(e => e.Name.LocalName == "EndBoundary")?.Value;
        return (start, end);
    }

    private static string DecodeToString(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            throw new FormatException("Task: empty or null input");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[1] == 0)
        {
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length);
        }
        return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
    }

    private static XDocument LoadDocument(byte[] bytes)
    {
        string text;
        try
        {
            text = DecodeToString(bytes);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("Malformed encoding: " + ex.Message, ex);
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        try
        {
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Malformed XML: " + ex.Message, ex);
        }
    }
}
