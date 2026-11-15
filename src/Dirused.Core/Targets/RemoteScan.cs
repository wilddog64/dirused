using System.ComponentModel;
using Dirused.Core.Volumes;
namespace Dirused.Core.Targets;
public static class RemoteScan
{
    public static ScanResult Scan(ResolvedTarget resolved, CancellationToken ct = default) => Scan(resolved, path => new Scanner().Scan(path, ct));
    public static ScanResult Scan(ResolvedTarget resolved, Func<string, ScanResult> scan)
    {
        var target = resolved.Target;
        string? failure = resolved.Error;
        if (failure == null)
        {
            try { return scan(target.Path) with { Target = target.Display }; }
            catch (VolumeProbeException ex) { failure = TargetResolver.ErrorMessage(target.Display, ex.NativeErrorCode); }
            catch (UnauthorizedAccessException) { failure = TargetResolver.ErrorMessage(target.Display, 5); }
            catch (Win32Exception ex) { failure = TargetResolver.ErrorMessage(target.Display, ex.NativeErrorCode); }
            catch (IOException ex) { failure = TargetResolver.ErrorMessage(target.Display, ex.HResult & 0xffff); }
        }
        return new ScanResult(target.Display, new Volume(target.Root, 0, 0, 0, 0), new(), new(), new() { new ScanError(target.Display, failure) }, false);
    }
}
