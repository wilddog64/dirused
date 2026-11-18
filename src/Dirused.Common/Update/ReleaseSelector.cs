using System.Text.RegularExpressions;

namespace Dirused.Common.Update;

public static class ReleaseSelector
{
    private static readonly Regex TagPattern = new("^v(\\d+)\\.(\\d+)\\.(\\d+)$", RegexOptions.Compiled);
    public static ReleasePackage? Select(IEnumerable<ReleaseInfo> releases, string rid)
    {
        return releases.Where(r => !r.Draft && TagPattern.IsMatch(r.TagName))
            .Select(r => Candidate(r, rid)).Where(p => p != null).Select(p => p!)
            .OrderByDescending(p => GitHubReleaseSource.Parse(p.Version)![0])
            .ThenByDescending(p => GitHubReleaseSource.Parse(p.Version)![1])
            .ThenByDescending(p => GitHubReleaseSource.Parse(p.Version)![2]).FirstOrDefault();
    }
    private static ReleasePackage? Candidate(ReleaseInfo release, string rid)
    {
        var m = TagPattern.Match(release.TagName); if (!m.Success) return null;
        var name = $"dirused-{release.TagName}-{rid}.zip";
        var zip = release.Assets.FirstOrDefault(a => a.Name == name); var hash = release.Assets.FirstOrDefault(a => a.Name == name + ".sha256");
        if (zip == null || hash == null) return null;
        return new ReleasePackage($"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}", release.TagName, rid, zip.Url, hash.Url, name);
    }
}
