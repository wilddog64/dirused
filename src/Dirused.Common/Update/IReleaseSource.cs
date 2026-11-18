namespace Dirused.Common.Update;

public sealed record ReleaseAsset(string Name, Uri Url);
public sealed record ReleaseInfo(string TagName, bool Draft, IReadOnlyList<ReleaseAsset> Assets);
public sealed record ReleasePackage(string Version, string Tag, string Rid, Uri ZipUrl, Uri HashUrl, string ZipName);

public interface IReleaseSource
{
    Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default);
}
