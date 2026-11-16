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
        var workers = new Task[Math.Min(maxParallel, targets.Count)];
        int nextIndex = -1;
        int abandoned = 0;
        var abandonedGate = new object();

        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(() =>
            {
                int idx;
                while ((idx = Interlocked.Increment(ref nextIndex)) < targets.Count)
                {
                    var target = targets[idx];
                    if (ct.IsCancellationRequested)
                    {
                        results[idx] = new TargetResult(target, Incomplete(target, "CANCELLED"));
                        continue;
                    }
                    if (timeout is { } duration)
                    {
                        lock (abandonedGate)
                        {
                            if (abandoned >= maxParallel)
                            {
                                results[idx] = new TargetResult(target, Incomplete(target, $"SKIPPED: {abandoned} earlier targets still hung past /timeout"));
                                continue;
                            }
                        }
                        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(duration);
                        using var done = new ManualResetEventSlim(false);
                        ScanResult? scanResult = null;
                        Exception? failure = null;
                        bool markedAbandoned = false;
                        var scanThread = new Thread(() =>
                        {
                            try { scanResult = scan(target, timeoutCts.Token); }
                            catch (Exception ex) { failure = ex; }
                            finally
                            {
                                done.Set();
                                lock (abandonedGate)
                                {
                                    if (markedAbandoned) abandoned--;
                                }
                            }
                        }) { IsBackground = true, Name = $"dirused-scan-{idx}" };
                        scanThread.Start();
                        TimeSpan wait = duration + TimeSpan.FromTicks(Math.Min(duration.Ticks, TimeSpan.FromSeconds(2).Ticks));
                        if (!done.Wait(wait))
                        {
                            lock (abandonedGate)
                            {
                                if (done.IsSet)
                                {
                                    // The scan completed as the wait expired; handle its result below.
                                }
                                else
                                {
                                    markedAbandoned = true;
                                    abandoned++;
                                }
                            }
                            if (markedAbandoned)
                            {
                                results[idx] = new TargetResult(target, Incomplete(target, $"TIMED OUT after {FormatDuration(duration)}"));
                                timeoutCts.Dispose();
                                continue;
                            }
                        }
                        bool timeoutRequested = timeoutCts.IsCancellationRequested;
                        timeoutCts.Dispose();
                        if (failure is OperationCanceledException && timeoutRequested && !ct.IsCancellationRequested)
                        {
                            results[idx] = new TargetResult(target, Incomplete(target, $"TIMED OUT after {FormatDuration(duration)}"));
                            continue;
                        }
                        if (failure is OperationCanceledException && ct.IsCancellationRequested)
                        {
                            results[idx] = new TargetResult(target, Incomplete(target, "CANCELLED"));
                            continue;
                        }
                        if (failure != null)
                        {
                            results[idx] = new TargetResult(target, Incomplete(target, failure.Message));
                            continue;
                        }
                        var result = scanResult!;
                        if (timeoutRequested && !ct.IsCancellationRequested)
                        {
                            var errors = result.Errors.ToList();
                            errors.Add(new ScanError(target.Display, $"TIMED OUT after {FormatDuration(duration)}"));
                            result = result with { Errors = errors, Complete = false };
                        }
                        else if (ct.IsCancellationRequested && !result.Errors.Any(e => e.Reason == "CANCELLED"))
                        {
                            var errors = result.Errors.ToList();
                            errors.Add(new ScanError(target.Display, "CANCELLED"));
                            result = result with { Errors = errors, Complete = false };
                        }
                        results[idx] = new TargetResult(target, result);
                        continue;
                    }

                    ScanResult unboundedResult;
                    try { unboundedResult = scan(target, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        results[idx] = new TargetResult(target, Incomplete(target, "CANCELLED"));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        results[idx] = new TargetResult(target, Incomplete(target, ex.Message));
                        continue;
                    }
                    if (ct.IsCancellationRequested && !unboundedResult.Errors.Any(e => e.Reason == "CANCELLED"))
                    {
                        var errors = unboundedResult.Errors.ToList();
                        errors.Add(new ScanError(target.Display, "CANCELLED"));
                        unboundedResult = unboundedResult with { Errors = errors, Complete = false };
                    }
                    results[idx] = new TargetResult(target, unboundedResult);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Task.WaitAll(workers);
        return results;
    }

    private static ScanResult Incomplete(Target target, string reason) =>
        new(target.Display, new Volume(target.Root, 0, 0, 0, 0), new List<Entry>(), new List<Entry>(), new List<ScanError> { new(target.Display, reason) }, false);

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";
}
