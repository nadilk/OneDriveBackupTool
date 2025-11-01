using OneDriveBackupTool.Models;

namespace OneDriveBackupTool.Configuration;

public class BackupMetadata
{
 public DateTime LastBackupTime { get; set; }
 public int TotalFilesBackedUp { get; set; }
 public Dictionary<string, OneDriveFile> Files { get; set; } = new();
}
