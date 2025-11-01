using OneDriveBackupTool.Models;

public class OneDriveUpdate
{
 public List<OneDriveFile> Files { get; set; } = new();
 public List<OneDriveFolder> Folders { get; set; } = new();
 public List<string> DeletedFileIds { get; set; } = new();
 public List<string> DeletedFolderIds { get; set; } = new();
}
