using System.Text.RegularExpressions;

namespace Dirused.Common;

public static class TrustCommands
{
    private static readonly Regex SidPattern = new(@"\AS-1-[0-9]+(-[0-9]+)+\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static int Trust(string? account, ITrustStore store, IAccountResolver accounts, TextWriter stdout, TextWriter stderr)
    {
        if (account == null)
        {
            var all = store.Load().OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToArray();
            if (all.Length == 0) stdout.WriteLine("No trusted accounts.");
            foreach (var a in all) stdout.WriteLine($"  {a.Sid}  {a.Display}");
            return 0;
        }
        var resolved = accounts.Resolve(account);
        TrustedAccount? found = resolved ?? (SidPattern.IsMatch(account) ? new TrustedAccount(account, "unresolved account " + account) : null);
        if (found == null) { stderr.WriteLine($"unknown account: {account}"); return 1; }
        if (PlanTrust.IsBroad(found.Sid)) { stderr.WriteLine($"{found.Display} is too wide to trust"); return 1; }
        if (store.Load().Any(x => string.Equals(x.Sid, found.Sid, StringComparison.OrdinalIgnoreCase))) { stdout.WriteLine($"{found.Display} is already trusted."); return 0; }
        var added = store.Add(found);
        if (!added.Ok) { stderr.WriteLine("error: could not save the trust list: " + added.Error); return 3; }
        stdout.WriteLine($"Trusted {found.Display} ({found.Sid})."); return 0;
    }
    public static int Untrust(string account, ITrustStore store, IAccountResolver accounts, TextWriter stdout, TextWriter stderr)
    {
        var sid = accounts.Resolve(account)?.Sid ?? (SidPattern.IsMatch(account) ? account : null);
        var stored = sid == null ? null : store.Load().FirstOrDefault(x => string.Equals(x.Sid, sid, StringComparison.OrdinalIgnoreCase));
        if (stored == null) { stderr.WriteLine($"{account} is not trusted."); return 1; }
        var removed = store.Remove(stored.Sid);
        if (!removed.Ok) { stderr.WriteLine("error: could not save the trust list: " + removed.Error); return 3; }
        stdout.WriteLine($"Removed {stored.Display}."); return 0;
    }
}
