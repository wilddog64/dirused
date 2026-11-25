using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using Dirused.Common;
using Xunit;

namespace Dirused.Tests;

[SupportedOSPlatform("windows")]
public sealed class ScheduleWindowsTests
{
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static string Temp() { string p=Path.Combine(Path.GetTempPath(),"dirused-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static void Run(string exe, params string[] args) { using var p=new Process{StartInfo=new(exe){UseShellExecute=false,CreateNoWindow=true}}; foreach(var a in args)p.StartInfo.ArgumentList.Add(a); p.Start(); p.WaitForExit(); Assert.Equal(0,p.ExitCode); }

    [Fact] public void SecureAndReadAcl_ReportsOwnerAndRules()
    {
        string root=Temp(); try { var sec=new RealPlanFolderSecurity(); Assert.True(sec.Secure(root,Sid).Ok); var acl=sec.ReadAcl(root); Assert.True(acl.Ok,acl.Error); Assert.Equal(Sid,acl.OwnerSid); var e=Assert.Single(acl.Entries); Assert.Equal(Sid,e.Sid); Assert.Equal(WriteRights.GenericAll,e.Rights); Assert.True(e.Allow); Assert.False(e.InheritOnly); } finally { Directory.Delete(root,true); }
    }
    [Fact] public void SecureAndReadAcl_ReportsEveryoneWriteAndInheritOnlyUsersModify()
    {
        string root=Temp();
        try
        {
            var security=new RealPlanFolderSecurity(); Assert.True(security.Secure(root,Sid).Ok);
            var directory=new DirectoryInfo(root); var acl=directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),FileSystemRights.Write,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.InheritOnly,AccessControlType.Allow));
            directory.SetAccessControl(acl);
            var read=security.ReadAcl(root); Assert.True(read.Ok,read.Error);
            Assert.Contains(read.Entries,e=>e.Sid=="S-1-1-0"&&(e.Rights&WriteRights.Write)!=0&&e.Allow);
            Assert.Contains(read.Entries,e=>e.Sid=="S-1-5-32-545"&&e.InheritOnly&&e.Allow);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public void ReadAcl_RejectsSubstDriveAndAcceptsCDrivePositiveControls()
    {
        string root=Temp(); char letter='\0';
        try
        {
            for(char candidate='Z';candidate>='D';candidate--) if(!Directory.Exists(candidate+":\\")){letter=candidate;break;}
            Assert.NotEqual('\0',letter); Run("subst.exe",letter+":",root);
            string existing=Path.Combine(root,"existing"); Directory.CreateDirectory(existing); string missing=Path.Combine(root,"missing");
            string substExisting=letter+@":\existing", substMissing=letter+@":\missing"; Assert.True(Directory.Exists(substExisting)); var security=new RealPlanFolderSecurity();
            foreach(string path in new[]{substExisting,substMissing}) { var acl=security.ReadAcl(path); Assert.False(acl.Ok); Assert.False(acl.Missing); Assert.Equal("not on a local fixed drive",acl.Error); }
            string cExisting="C:"+existing[2..], cMissing="C:"+missing[2..]; var existingAcl=security.ReadAcl(cExisting); Assert.True(existingAcl.Ok,existingAcl.Error); Assert.False(existingAcl.Missing); var missingAcl=security.ReadAcl(cMissing); Assert.False(missingAcl.Ok); Assert.True(missingAcl.Missing);
        }
        finally { if(letter!='\0') { try { Run("subst.exe",letter+":","/d"); } catch { } } Directory.Delete(root,true); }
    }
    [Fact] public void Store_EnforcesNewAndAppendSemantics()
    {
        string root=Temp(); try { var s=new RealPlanStore(Path.Combine(root,"logs","purge.log")); string f=Path.Combine(root,"x"), other=Path.Combine(root,"y"); File.WriteAllBytes(f,[1,2]); Assert.False(s.WriteNew(f,[3]).Ok); Assert.Equal(new byte[]{1,2},File.ReadAllBytes(f)); File.WriteAllBytes(other,[4]); Assert.False(s.MoveNew(other,f).Ok); Assert.Equal(new byte[]{1,2},File.ReadAllBytes(f)); Assert.True(s.MoveNew(other,Path.Combine(root,"z")).Ok); Assert.Equal("already exists",s.CreateDirectory(root).Error); Assert.True(s.DeleteTree(Path.Combine(root,"missing")).Ok); Assert.Null(s.ReadAll(Path.Combine(root,"missing"))); Assert.True(s.AppendLog("one").Ok); Assert.True(s.AppendLog("two").Ok); Assert.Equal(new[]{"one","two"},File.ReadAllLines(Path.Combine(root,"logs","purge.log"))); } finally { Directory.Delete(root,true); }
    }
    [Fact] public void Store_IsReparsePoint_IdentifiesJunctionPlainAndMissingPaths()
    {
        string parent=Temp(), target=Path.Combine(parent,"target"), junction=Path.Combine(parent,"junction"), missing=Path.Combine(parent,"missing"); Directory.CreateDirectory(target);
        try { var store=new RealPlanStore(Path.Combine(parent,"log")); Run("cmd.exe","/c","mklink","/J",junction,target); Assert.True(store.IsReparsePoint(junction)); Assert.False(store.IsReparsePoint(target)); Assert.False(store.IsReparsePoint(missing)); Assert.True(store.IsReparsePoint(Path.Combine(parent,"bad\0name"))); }
        finally { if(Directory.Exists(junction))Directory.Delete(junction); Directory.Delete(parent,true); }
    }
    [Fact] public void Resolver_ResolvesSidAndRejectsUnknownNames()
    {
        var resolver=new RealAccountResolver(); string name=WindowsIdentity.GetCurrent().Name!; Assert.Equal(Sid,resolver.Resolve(name)?.Sid); Assert.Equal(name,resolver.Resolve(Sid)?.Display); Assert.Null(resolver.Resolve("S-1-5-21-1-2-3-999999")); Assert.Null(resolver.Resolve("no such account x"));
    }
    [Fact] public void WindowsSchedule_UsesCurrentProcessAndProfile()
    {
        var s=WindowsSchedule.Create("1.2.3"); Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dirused","plans"),s.PlanRoot); Assert.Equal(Sid,s.UserSid); Assert.Equal(Environment.ProcessPath,s.ExePath); Assert.Equal(Environment.ProcessPath,s.ProcessImagePath(Environment.ProcessId)); Assert.Null(s.ProcessImagePath(-1));
    }
    [Fact] public void ProcessRunner_CapturesAndTimesOut()
    {
        var runner=new RealProcessRunner(2000); var start=DateTime.UtcNow; Assert.Equal(-1,runner.Run("powershell.exe",["-NoProfile","-Command","Start-Sleep 30"]).ExitCode); Assert.True(DateTime.UtcNow-start<TimeSpan.FromSeconds(10)); Assert.Equal(-1,runner.Run("dirused-does-not-exist.exe",[]).ExitCode); var result=runner.Run("cmd.exe",["/c","echo hi"]); Assert.Equal(0,result.ExitCode); Assert.Contains("hi",Encoding.Default.GetString(result.Stdout));
    }
    [Fact] public void TrustStore_AddLoadRemove()
    {
        string root=Temp(); try { var sec=new RealPlanFolderSecurity(); var store=new RealTrustStore(Path.Combine(root,"trust"),sec,Sid); var item=new TrustedAccount("S-1-5-18","SYSTEM"); Assert.True(store.Add(item).Ok); Assert.Contains(item,store.Load()); var acl=sec.ReadAcl(Path.Combine(root,"trust")); Assert.True(acl.Ok,acl.Error); Assert.All(acl.Entries,e=>Assert.Equal(Sid,e.Sid)); Assert.True(store.Remove(item.Sid).Ok); Assert.Empty(store.Load()); } finally { Directory.Delete(root,true); }
    }
    [Fact] public void TrustStore_LoadRejectsEveryoneAllowAceOnTrustFile()
    {
        string root=Temp(), folder=Path.Combine(root,"trust");
        try
        {
            var security=new RealPlanFolderSecurity(); var store=new RealTrustStore(folder,security,Sid); Assert.True(store.Add(new("S-1-5-18","SYSTEM")).Ok); Assert.NotEmpty(store.Load());
            string trustPath=Path.Combine(folder,"trust.txt"); var file=new FileInfo(trustPath); var acl=file.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),FileSystemRights.Write,AccessControlType.Allow)); file.SetAccessControl(acl);
            Assert.Empty(store.Load());
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public void TrustStore_RejectsJunctionWithoutChangingTarget()
    {
        string parent=Temp(), target=Path.Combine(parent,"T"), junction=Path.Combine(parent,"J"); Directory.CreateDirectory(target);
        try
        {
            var sec=new RealPlanFolderSecurity(); var original=new RealTrustStore(target,sec,Sid); Assert.True(original.Add(new("S-1-5-18","SYSTEM")).Ok); Assert.NotEmpty(original.Load());
            var before=sec.ReadAcl(target); Assert.True(before.Ok,before.Error); byte[] trustBefore=File.ReadAllBytes(Path.Combine(target,"trust.txt"));
            Run("cmd.exe","/c","mklink","/J",junction,target);
            var throughLink=new RealTrustStore(junction,sec,Sid); Assert.Empty(throughLink.Load()); var result=throughLink.Add(new("S-1-5-18","SYSTEM")); Assert.False(result.Ok); Assert.Equal(junction+" is a link",result.Error);
            var after=sec.ReadAcl(target); Assert.True(after.Ok,after.Error); Assert.Equal(before.OwnerSid,after.OwnerSid); Assert.Equal(before.Entries.Select(e=>(e.Sid,e.Rights,e.Allow,e.InheritOnly)),after.Entries.Select(e=>(e.Sid,e.Rights,e.Allow,e.InheritOnly))); Assert.Equal(trustBefore,File.ReadAllBytes(Path.Combine(target,"trust.txt")));
        }
        finally { if(Directory.Exists(junction))Directory.Delete(junction); Directory.Delete(parent,true); }
    }
    [Fact] public void Scheduler_RegistersRejectsDuplicateAndDeletesCiTask()
    {
        string root=Temp(), id="20261231-1200-"+Guid.NewGuid().ToString("N")[..4], taskName=$@"\dirused-ci\{Sid}\purge-{Guid.NewGuid():N}";
        DateTimeOffset run=new(DateTimeOffset.Now.Ticks/TimeSpan.TicksPerSecond*TimeSpan.TicksPerSecond,DateTimeOffset.Now.Offset); run=run.AddDays(1); var expected=new PlanTask(id,run,run.AddHours(24),Sid,root,"dirused scheduled purge test"); string xmlPath=Path.Combine(root,"task.xml");
        try
        {
            File.WriteAllBytes(xmlPath,TaskXml.Build(expected)); var scheduler=new SchtasksTaskScheduler(new RealProcessRunner(),Environment.SystemDirectory);
            Assert.True(scheduler.Register(taskName,xmlPath,expected).Ok); var duplicate=scheduler.Register(taskName,xmlPath,expected); Assert.False(duplicate.Ok); Assert.Matches(new Regex(@"could not schedule \(schtasks exit [1-9][0-9]*\)"),duplicate.Error!); Assert.True(scheduler.Exists(taskName)); Assert.True(scheduler.Delete(taskName).Ok); Assert.False(scheduler.Exists(taskName));
        }
        finally { try { new SchtasksTaskScheduler(new RealProcessRunner(),Environment.SystemDirectory).Delete(taskName); } catch { } Directory.Delete(root,true); }
    }
}
