namespace Dirused.Common;

public sealed record ProcessResult(int ExitCode, byte[] Stdout);

public interface IProcessRunner
{
    ProcessResult Run(string exe, IReadOnlyList<string> args);
}

public sealed class SchtasksTaskScheduler(IProcessRunner runner, string systemDirectory) : ITaskScheduler
{
    private readonly string _exe = systemDirectory.TrimEnd('\\') + "\\schtasks.exe";

    public SeamResult Register(string taskName, string xmlPath, PlanTask expected)
    {
        var create = Run(["/create", "/tn", taskName, "/xml", xmlPath]);
        if (create.ExitCode != 0)
            return new(false, $"could not schedule (schtasks exit {create.ExitCode})");

        var query = Run(["/query", "/tn", taskName, "/xml"]);
        string? field = null;
        if (query.ExitCode != 0)
        {
            field = $"query exit {query.ExitCode}";
        }
        else
        {
            PlanTask actual;
            try
            {
                actual = TaskXml.Parse(query.Stdout);
            }
            catch (FormatException ex)
            {
                var colon = ex.Message.IndexOf(':');
                field = colon < 0 ? ex.Message : ex.Message[..colon];
                actual = expected;
            }

            if (field == null)
            {
                if (!string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)) field = "Id";
                else if (actual.Run.ToUniversalTime() != expected.Run.ToUniversalTime()) field = "Run";
                else if (actual.Expires.ToUniversalTime() != expected.Expires.ToUniversalTime()) field = "Expires";
                else if (!string.Equals(actual.UserSid, expected.UserSid, StringComparison.OrdinalIgnoreCase)) field = "UserSid";
                else if (!string.Equals(actual.PlanDir, expected.PlanDir, StringComparison.OrdinalIgnoreCase)) field = "PlanDir";
                else if (!string.Equals(actual.Description, expected.Description, StringComparison.Ordinal)) field = "Description";
                else
                {
                    try
                    {
                        var descriptor = TaskXml.SecurityDescriptorOf(query.Stdout);
                        var wanted = $"D:P(A;;FA;;;{expected.UserSid})(A;;FA;;;SY)(A;;FA;;;BA)";
                        if (!string.Equals(descriptor, wanted, StringComparison.Ordinal)) field = "SecurityDescriptor";
                    }
                    catch (FormatException ex)
                    {
                        var colon = ex.Message.IndexOf(':');
                        field = colon < 0 ? ex.Message : ex.Message[..colon];
                    }
                }
            }
        }

        if (field == null) return new(true);

        var delete = Run(["/delete", "/tn", taskName, "/f"]);
        return delete.ExitCode == 0
            ? new(false, $"scheduled task does not match the plan ({field}); it was removed")
            : new(false, $"scheduled task does not match the plan ({field}); removing it failed (schtasks exit {delete.ExitCode})");
    }

    public bool Exists(string taskName) => Run(["/query", "/tn", taskName]).ExitCode == 0;

    public SeamResult Delete(string taskName)
    {
        var delete = Run(["/delete", "/tn", taskName, "/f"]);
        if (delete.ExitCode == 0) return new(true);
        return Exists(taskName) ? new(false, $"schtasks exit {delete.ExitCode}") : new(true);
    }

    private ProcessResult Run(IReadOnlyList<string> args)
    {
        try { return runner.Run(_exe, args); }
        catch { return new(-1, []); }
    }
}
