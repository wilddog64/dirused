using System.Globalization;
using Dirused.Common.Targets;
using Dirused.Core;
namespace Dirused.Common;
public record RunOptions(int? Parallel = null, TimeSpan? Timeout = null);
public record TargetResult(Target Target, ScanResult Result);
public static class ScanRunner
{
    public static IReadOnlyList<TargetResult> Run(IReadOnlyList<Target> targets, Func<Target, CancellationToken, ScanResult> scan, RunOptions options, CancellationToken ct)
    {
        if (targets.Count == 0) return Array.Empty<TargetResult>();
        var results = new TargetResult[targets.Count];
        int maxParallel = Math.Clamp(options.Parallel ?? 4, 1, 64);
        TimeSpan? timeout = options.Timeout is { } t && t > TimeSpan.Zero ? t : null;
        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = new Task[targets.Count];

        for (int i = 0; i < targets.Count; i++)
        {
            int idx = i;
            var target = targets[idx];
            tasks[idx] = Task.Run(async () =>
            {
                bool acquired = false;
                try
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;
                    using var timeoutCts = timeout is { } d ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
                    if (timeoutCts != null && timeout is { } dur) timeoutCts.CancelAfter(dur);
                    var scanCt = timeoutCts?.Token ?? ct;
                    bool timedOut = false;
                    ScanResult result;
                    try
                    {
                        result = scan(target, scanCt);
                        timedOut = timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                    }
                    catch (OperationCanceledException) when (timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        timedOut = true;
                        result = Incomplete(target, $"TIMED OUT after {FormatDuration(timeout!.Value)}");
                        results[idx] = new TargetResult(target, result);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        results[idx] = new TargetResult(target, Incomplete(target, "CANCELLED"));
                        return;
                    }
                    catch (Exception ex)
                    {
                        results[idx] = new TargetResult(target, Incomplete(target, ex.Message));
                        return;
                    }

                    if (timedOut)
                    {
                        var errors = result.Errors.ToList();
                        errors.Add(new ScanError(target.Display, $"TIMED OUT after {FormatDuration(timeout!.Value)}"));
                        result = result with { Errors = errors, Complete = false };
                    }
                    else if (ct.IsCancellationRequested && result.Complete)
                    {
                        var errors = result.Errors.ToList();
                        errors.Add(new ScanError(target.Display, "CANCELLED"));
                        result = result with { Errors = errors, Complete = false };
                    }

                    results[idx] = new TargetResult(target, result);
                }
                catch (OperationCanceledException)
                {
                    results[idx] = new TargetResult(target, Incomplete(target, "CANCELLED"));
                }
                finally
                {
                    if (acquired) semaphore.Release();
                }
            }, CancellationToken.None);
        }

        Task.WaitAll(tasks);
        return results;
    }

    private static ScanResult Incomplete(Target target, string reason) =>
        new(target.Display, new Volume(target.Root, 0, 0, 0, 0), new List<Entry>(), new List<Entry>(), new List<ScanError> { new(target.Display, reason) }, false);

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";
}
