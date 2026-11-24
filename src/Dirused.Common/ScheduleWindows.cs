using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Dirused.Common;

[SupportedOSPlatform("windows")]
public sealed class RealProcessRunner(int timeoutMs = 60000) : IProcessRunner
{
    public ProcessResult Run(string exe, IReadOnlyList<string> args)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return new(-1, []);
            using var output = new MemoryStream();
            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                try { Task.WaitAll([stdout, stderr], 5000); } catch { }
                return new(-1, []);
            }
            Task.WaitAll(stdout, stderr);
            return new(process.ExitCode, output.ToArray());
        }
        catch { return new(-1, []); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class RealPlanStore(string purgeLogPath) : IPlanStore
{
    public bool Exists(string path) { try { return File.Exists(path) || Directory.Exists(path); } catch { return false; } }
    public bool IsReparsePoint(string path) { try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } catch { return true; } }
    public SeamResult CreateDirectory(string path) { try { if (Exists(path)) return new(false, "already exists"); Directory.CreateDirectory(path); return new(true); } catch (Exception ex) { return new(false, ex.Message); } }
    public SeamResult WriteNew(string path, byte[] bytes) { try { using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); f.Write(bytes); f.Flush(); return new(true); } catch (Exception ex) { return new(false, ex.Message); } }
    public SeamResult MoveNew(string from, string to) { try { File.Move(from, to, false); return new(true); } catch (Exception ex) { return new(false, ex.Message); } }
    public byte[]? ReadAll(string path) { try { return File.ReadAllBytes(path); } catch { return null; } }
    public IReadOnlyList<string> ListDirectoryNames(string path) { try { return Directory.GetDirectories(path).Select(Path.GetFileName).Where(x => x != null).Cast<string>().ToArray(); } catch (DirectoryNotFoundException) { return []; } catch { return []; } }
    public SeamResult DeleteTree(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); return new(true); } catch (Exception ex) { return new(false, ex.Message); } }
    public SeamResult AppendLog(string line) { try { string? folder = Path.GetDirectoryName(purgeLogPath); if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder); using var f = new FileStream(purgeLogPath, FileMode.Append, FileAccess.Write, FileShare.Read); using var w = new StreamWriter(f, new UTF8Encoding(false)); w.Write(line); w.Write(Environment.NewLine); return new(true); } catch (Exception ex) { return new(false, ex.Message); } }
}

[SupportedOSPlatform("windows")]
public sealed class RealPlanFolderSecurity : IPlanFolderSecurity
{
    private readonly Dictionary<string,string> _display = new(StringComparer.OrdinalIgnoreCase);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDeviceW(string lpDeviceName, char[] lpTargetPath, uint ucchMax);
    private string Display(SecurityIdentifier sid) { if (_display.TryGetValue(sid.Value, out string? value)) return value; try { value = sid.Translate(typeof(NTAccount)).Value; } catch (IdentityNotMappedException) { value = ""; } catch { value = ""; } _display[sid.Value] = value; return value; }
    public AclRead ReadAcl(string path)
    {
        try
        {
            if (path.Length < 3 || !Regex.IsMatch(path, @"\A[A-Za-z]:\\")) return new(false,false,null,null,[],"not on a local fixed drive");
            string drive = path[..3];
            if (new DriveInfo(drive).DriveType != DriveType.Fixed) return new(false,false,null,null,[],"not on a local fixed drive");
            char[] target = new char[1024];
            if (QueryDosDeviceW(drive[..2], target, (uint)target.Length) == 0 || new string(target).Split('\0')[0].StartsWith(@"\??\", StringComparison.Ordinal)) return new(false,false,null,null,[],"not on a local fixed drive");
            bool file = File.Exists(path), dir = Directory.Exists(path);
            if (!file && !dir) return new(false,true,null,null,[],null);
            FileSystemSecurity sec = file ? new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner) : new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var owner = (SecurityIdentifier)sec.GetOwner(typeof(SecurityIdentifier))!;
            var raw = new RawSecurityDescriptor(sec.GetSecurityDescriptorBinaryForm(), 0);
            var entries = new List<AclEntry>();
            if (raw.DiscretionaryAcl == null) entries.Add(new("S-1-1-0", "Everyone", WriteRights.GenericAll, false, true));
            else foreach (FileSystemAccessRule rule in sec.GetAccessRules(true,true,typeof(SecurityIdentifier))) { var sid=(SecurityIdentifier)rule.IdentityReference; entries.Add(new(sid.Value,Display(sid),AclRights.FromMask((int)rule.FileSystemRights),rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly),rule.AccessControlType==AccessControlType.Allow)); }
            return new(true,false,owner.Value,Display(owner),entries,null);
        }
        catch (Exception ex) { return new(false,false,null,null,[],ex.Message); }
    }
    public SeamResult Secure(string path, string userSid)
    {
        try
        {
            var wanted = new SecurityIdentifier(userSid);
            var current = ReadAcl(path);
            if (!current.Ok) return new(false,current.Error);
            var security = new DirectorySecurity();
            if (!string.Equals(current.OwnerSid,userSid,StringComparison.OrdinalIgnoreCase)) security.SetOwner(wanted);
            security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(wanted,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return new(true);
        }
        catch (Exception ex) { return new(false,ex.Message); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class RealTrustStore(string folder, IPlanFolderSecurity security, string userSid) : ITrustStore
{
    private string FilePath => Path.Combine(folder,"trust.txt");
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public IReadOnlyList<TrustedAccount> Load()
    {
        try
        {
            if (IsReparsePoint(folder) || IsReparsePoint(FilePath)) return [];
            if (!Directory.Exists(folder) || !File.Exists(FilePath) || security.ReadAcl(folder) is not { Ok:true, OwnerSid:var fo } fa || !string.Equals(fo,userSid,StringComparison.OrdinalIgnoreCase) || security.ReadAcl(FilePath) is not { Ok:true, OwnerSid:var to } ta || !string.Equals(to,userSid,StringComparison.OrdinalIgnoreCase) || !UserOnly(fa) || !UserOnly(ta)) return [];
            return File.ReadAllLines(FilePath,new UTF8Encoding(false,true)).Select(line => { int i=line.IndexOf('\t'); return i>0 ? new TrustedAccount(line[..i],line[(i+1)..]) : null; }).Where(x=>x!=null).Cast<TrustedAccount>().ToArray();
        } catch { return []; }
    }
    private bool UserOnly(AclRead acl) => acl.Entries.Where(e=>e.Allow).All(e=>string.Equals(e.Sid,userSid,StringComparison.OrdinalIgnoreCase));
    private SeamResult Save(TrustedAccount[] items)
    {
        try { string tmp=Path.Combine(folder,"trust.tmp"); if (File.Exists(tmp)) File.Delete(tmp); var created=new RealPlanStore(Path.Combine(folder,"unused")).WriteNew(tmp,new UTF8Encoding(false).GetBytes(string.Concat(items.Select(x=>x.Sid+"\t"+x.Display+"\n")))); if(!created.Ok)return created; File.Move(tmp,FilePath,true); return new(true); } catch(Exception ex){return new(false,ex.Message);}
    }
    public SeamResult Add(TrustedAccount account) => Change(account.Sid,account,true);
    public SeamResult Remove(string sid) => Change(sid,null,false);
    private SeamResult Change(string sid,TrustedAccount? account,bool add)
    {
        try { if(IsReparsePoint(folder))return new(false,folder+" is a link"); string trustPath=FilePath; if(IsReparsePoint(trustPath))return new(false,trustPath+" is a link"); Directory.CreateDirectory(folder); var secured=security.Secure(folder,userSid); if(!secured.Ok)return secured; var list=Load().Where(x=>!string.Equals(x.Sid,sid,StringComparison.OrdinalIgnoreCase)).ToList(); if(add && account!=null)list.Add(account); return Save(list.ToArray()); } catch(Exception ex){return new(false,ex.Message);}
    }
}

[SupportedOSPlatform("windows")]
public sealed class RealAccountResolver : IAccountResolver
{
    private static readonly Regex SidPattern=new(@"\AS-1-[0-9]+(-[0-9]+)+\z",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    public TrustedAccount? Resolve(string nameOrSid)
    {
        try { if(SidPattern.IsMatch(nameOrSid)){var sid=new SecurityIdentifier(nameOrSid); return new(sid.Value,((NTAccount)sid.Translate(typeof(NTAccount))).Value);} var translated=(SecurityIdentifier)new NTAccount(nameOrSid).Translate(typeof(SecurityIdentifier)); return new(translated.Value,((NTAccount)translated.Translate(typeof(NTAccount))).Value); } catch{return null;}
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsSchedule
{
    public static ScheduleSeams Create(string toolVersion)
    {
        string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder=Path.Combine(local,"dirused"); string sid=WindowsIdentity.GetCurrent().User!.Value; var security=new RealPlanFolderSecurity();
        return new(()=>DateTimeOffset.Now,TimeZoneInfo.Local,Path.Combine(folder,"plans"),sid,Environment.ProcessPath??"",toolVersion,()=>RandomNumberGenerator.GetInt32(0x10000),new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)?TokenLevel.High:TokenLevel.Medium,new RealPlanStore(PurgeLog.DefaultPath),security,new SchtasksTaskScheduler(new RealProcessRunner(),Environment.SystemDirectory),new RealTrustStore(folder,security,sid),new RealAccountResolver(),pid=>{try{return Process.GetProcessById(pid).MainModule?.FileName;}catch{return null;}});
    }
    public static RunPlanSeams RunSeams() => new(new RealPurgeFileSystem(),new RealServiceQuery(),null,()=>DateTime.UtcNow,Environment.ProcessId,PurgeLog.DefaultPath,Thread.Sleep);
}
