namespace Dirused.Common.Update;

public sealed class UpdateCheck
{
    private readonly IReleaseSource _source;
    private readonly TextWriter _error;
    private readonly string _statePath, _version, _rid;
    private readonly Func<DateTimeOffset> _clock;
    private UpdateState? _pendingNoticeState;

    public UpdateCheck(IReleaseSource source, TextWriter error, string statePath, string version, string rid, Func<DateTimeOffset>? clock = null)
    { _source = source; _error = error; _statePath = statePath; _version = version; _rid = rid; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public async Task CheckAsync()
    {
        if (GitHubReleaseSource.Parse(_version) == null || Environment.GetEnvironmentVariable("DIRUSED_NO_UPDATE_CHECK") == "1") return;
        var now = _clock();
        var state = UpdateState.Read(_statePath, now);
        if (state.AttemptedUtc is { } attempted && now - attempted <= TimeSpan.FromHours(24))
        {
            _pendingNoticeState = state;
            return;
        }

        state = state with { AttemptedUtc = now };
        TryWrite(state);
        try
        {
            var found = await _source.FindLatestAsync(_version, _rid).WaitAsync(TimeSpan.FromSeconds(10));
            if (found != null) state = state with { Latest = found.Version };
        }
        catch { }
        TryWrite(state);
        _pendingNoticeState = state;
    }

    public void WaitAfterReport(Task? inFlightCheck, TimeSpan grace)
    {
        try { inFlightCheck?.Wait(grace); } catch { }
        if (inFlightCheck != null && !inFlightCheck.IsCompleted) return;
        var state = _pendingNoticeState;
        _pendingNoticeState = null;
        if (state != null) PrintNotice(state, _clock());
    }

    private void PrintNotice(UpdateState state, DateTimeOffset now)
    {
        if (GitHubReleaseSource.Parse(state.Latest ?? "") is not { } latest || GitHubReleaseSource.Compare(latest, GitHubReleaseSource.Parse(_version)) <= 0) return;
        if (state.NoticedUtc is { } noticed && now - noticed <= TimeSpan.FromHours(24)) return;
        _error.WriteLine($"dirused {string.Join('.', latest)} is available (you have {_version}). Run: dirused /update");
        TryWrite(state with { NoticedUtc = now });
    }

    private void TryWrite(UpdateState state) { try { state.Write(_statePath); } catch { } }
}
