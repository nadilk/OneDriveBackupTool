namespace OneDriveBackupTool.Models;

public class OneDriveFolder
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Excluded { get; set; } = false;
}
