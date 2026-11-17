using Dirused.Common.Renderers;
using Dirused.Common.Targets;
using Dirused.Core;

namespace Dirused.Common;

public static class TopFolders
{
    public const int Count = 3;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<ScanResult> Attach(IReadOnlyList<Target> targets, IReadOnlyList<ScanResult> results,
        Func<Target, CancellationToken, ScanResult> scan, RunOptions options, CancellationToken ct)
    {
        var indices = Enumerable.Range(0, results.Count).Where(i => results[i].Level is FullnessLevel.Warn or FullnessLevel.Crit).ToList();
        if (indices.Count == 0) return results;
        var selected = indices.Select(i => targets[i]).ToList();
        var scans = ScanRunner.Run(selected, scan, options, ct);
        var attached = results.ToList();
        for (int j = 0; j < indices.Count; j++)
        {
            var result = scans[j].Result;
            string? note = result.Complete ? null : result.Errors.FirstOrDefault() is { } error ? $"{error.Path}: {error.Reason}" : $"{result.Target}: Unknown error";
            var directories = result.Directories.OrderByDescending(e => e.LogicalBytes)
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(Count).ToList();
            attached[indices[j]] = attached[indices[j]] with { Directories = directories, TopFoldersNote = note };
        }
        return attached;
    }
}
