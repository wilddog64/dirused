using Dirused.Common.Targets;
using Dirused.Core;
namespace Dirused.Common;
public record RunOptions(int? Parallel = null, TimeSpan? Timeout = null);
public record TargetResult(Target Target, ScanResult Result);
public static class ScanRunner
{
    public static IReadOnlyList<TargetResult> Run(IReadOnlyList<Target> targets, Func<Target, CancellationToken, ScanResult> scan, RunOptions options, CancellationToken ct)
        => targets.Select(target => new TargetResult(target, scan(target, ct))).ToList();
}
