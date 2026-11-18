namespace Dirused.Common.Update;

public interface IUpdateFileOperations
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    void MoveFile(string source, string destination);
    Stream CreateFile(string path);
}

public sealed class PhysicalUpdateFileOperations : IUpdateFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public void MoveFile(string source, string destination) => File.Move(source, destination);
    public Stream CreateFile(string path) => File.Create(path);
}
