namespace Dirused.Common.Targets;
public enum TargetKind { LocalPath, LocalDrive, Unc, Node }
public record Target(string Display, TargetKind Kind, string Root, string Path);
