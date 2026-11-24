namespace Dirused.Common;

public sealed record SeamResult(bool Ok, string? Error = null);
[Flags] public enum WriteRights { None = 0, Write = 1, Append = 2, Delete = 4, DeleteChild = 8, WriteDac = 16, WriteOwner = 32, GenericWrite = 64, GenericAll = 128 }
public sealed record AclEntry(string Sid, string Display, WriteRights Rights, bool InheritOnly, bool Allow);
public sealed record AclRead(bool Ok, bool Missing, string? OwnerSid, string? OwnerDisplay, IReadOnlyList<AclEntry> Entries, string? Error);
public sealed record TrustedAccount(string Sid, string Display);
public enum TokenLevel { Medium, High }
public interface IPlanStore
{
    bool Exists(string path);
    bool IsReparsePoint(string path);
    SeamResult CreateDirectory(string path);
    SeamResult WriteNew(string path, byte[] bytes);
    SeamResult MoveNew(string from, string to) => new(false, "move unavailable");
    byte[]? ReadAll(string path);
    IReadOnlyList<string> ListDirectoryNames(string path);
    SeamResult DeleteTree(string path);
    SeamResult AppendLog(string line);
}
public interface IPlanFolderSecurity { SeamResult Secure(string path, string userSid); AclRead ReadAcl(string path); }
public interface ITaskScheduler { SeamResult Register(string taskName, string xmlPath, PlanTask expected); bool Exists(string taskName); SeamResult Delete(string taskName); }
public interface ITrustStore { IReadOnlyList<TrustedAccount> Load(); SeamResult Add(TrustedAccount account); SeamResult Remove(string sid); }
public interface IAccountResolver { TrustedAccount? Resolve(string nameOrSid); }
public sealed record ScheduleSeams(Func<DateTimeOffset> Now, TimeZoneInfo Local, string PlanRoot, string UserSid, string ExePath, string ToolVersion, Func<int> IdSuffix, TokenLevel Token, IPlanStore Store, IPlanFolderSecurity Security, ITaskScheduler Scheduler, ITrustStore Trust, IAccountResolver Accounts, Func<int, string?> ProcessImagePath);
public sealed record ScheduleRequest(Options Options, byte[] ListBytes, string ListPathAsTyped, string ListFullPath);
