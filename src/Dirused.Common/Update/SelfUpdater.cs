using System.IO.Compression;

namespace Dirused.Common.Update;

public static class SelfUpdater
{
    public static void Cleanup(string exePath, IUpdateFileOperations? fileOperations = null)
    {
        if (!Path.GetFileName(exePath).Equals("dirused.exe", StringComparison.OrdinalIgnoreCase)) return;
        var files = fileOperations ?? new PhysicalUpdateFileOperations();
        string dir = Path.GetDirectoryName(exePath)!, lockPath = Path.Combine(dir, "dirused.update.lock");
        try
        {
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try { files.DeleteFile(exePath + ".old"); } catch { }
                try { files.DeleteDirectory(Path.Combine(dir, ".dirused-update"), true); } catch { }
            }
        }
        catch { return; }
        try { File.Delete(lockPath); } catch { }
    }
    public static async Task<int> RunAsync(IReleaseSource source, string version, string rid, string exePath, TextWriter output, TextWriter error, IUpdateFileOperations? fileOperations = null)
    {
        if (GitHubReleaseSource.Parse(version) == null) { error.WriteLine("Cannot update a development or unrecognized version."); return 1; }
        if (!Path.GetFileName(exePath).Equals("dirused.exe", StringComparison.OrdinalIgnoreCase)) { error.WriteLine("/update must run from dirused.exe."); return 1; }
        string dir = Path.GetDirectoryName(exePath)!, lockPath = Path.Combine(dir, "dirused.update.lock"), stage = Path.Combine(dir, ".dirused-update"), old = exePath + ".old";
        var files = fileOperations ?? new PhysicalUpdateFileOperations();
        FileStream? held = null;
        try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { error.WriteLine("Another update is running."); return 1; }
        catch (Exception ex) { error.WriteLine(ex.Message); return 1; }
        try
        {
            files.CreateDirectory(stage);
            var package = await source.FindLatestAsync(version, rid);
            if (package == null) { error.WriteLine("No newer release with assets for this architecture was found."); return 1; }
            if (GitHubReleaseSource.Compare(GitHubReleaseSource.Parse(package.Version), GitHubReleaseSource.Parse(version)) <= 0) { output.WriteLine($"dirused {version} is up to date"); return 0; }
            if (!ValidAssetUrl(package.ZipUrl) || !ValidAssetUrl(package.HashUrl)) throw new InvalidDataException("Asset URL is not allowed.");
            byte[] zip = await source.DownloadAsync(package.ZipUrl, 100_000_000).WaitAsync(TimeSpan.FromSeconds(120));
            byte[] hashFile = await source.DownloadAsync(package.HashUrl, 100_000_000).WaitAsync(TimeSpan.FromSeconds(120));
            if (zip.Length > 100_000_000 || hashFile.Length > 100_000_000) throw new InvalidDataException("Download exceeds size limit.");
            if (Sha256File.Hash(zip) != Sha256File.Parse(System.Text.Encoding.UTF8.GetString(hashFile), package.ZipName)) throw new InvalidDataException("Downloaded zip SHA-256 does not match.");
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            if (archive.Entries.Any(e => e.FullName.Contains("..") || Path.IsPathRooted(e.FullName) || e.FullName.Contains(':'))) throw new InvalidDataException("Unsafe zip entry path.");
            var entries = archive.Entries.Where(e => e.FullName == "dirused.exe").ToList(); if (entries.Count != 1) throw new InvalidDataException("Zip must contain exactly one root dirused.exe.");
            var staged = Path.Combine(stage, "dirused.exe"); await using (var input = entries[0].Open()) await using (var dest = files.CreateFile(staged)) await input.CopyToAsync(dest);
            if (files.FileExists(old)) files.DeleteFile(old);
            files.MoveFile(exePath, old);
            try { files.MoveFile(staged, exePath); }
            catch { try { files.MoveFile(old, exePath); } catch { error.WriteLine($"Restore the executable from {old}."); } throw; }
            output.WriteLine($"updated dirused {version} -> {package.Version}"); return 0;
        }
        catch (Exception ex) { error.WriteLine(ex.Message); return 1; }
        finally { try { if (files.DirectoryExists(stage)) files.DeleteDirectory(stage, true); } catch { } held.Dispose(); try { File.Delete(lockPath); } catch { } }
    }
    private static bool ValidAssetUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/wilddog64/dirused/releases/download/", StringComparison.Ordinal);
}
