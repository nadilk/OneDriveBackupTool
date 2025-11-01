namespace OneDriveBackupTool.Models;

public class OneDriveFile
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string ETag { get; set; } = string.Empty;
    public string CTag { get; set; } = string.Empty;
    public bool Excluded { get; set; } = false;
}
