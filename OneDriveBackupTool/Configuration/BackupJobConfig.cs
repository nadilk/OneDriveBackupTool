namespace OneDriveBackupTool.Configuration;

public class BackupJobConfig
{
    const int DefaultMaxConcurrency = 4;
    const int DefaultBackupIntervalMinutes = 60;


    public string AccountName { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string OneDriveDirectory { get; set; } = string.Empty;
    public string LocalTargetDirectory { get; set; } = string.Empty;
    public int BackupIntervalMinutes { get; set; } = DefaultBackupIntervalMinutes;
    public List<string> Excluded { get; set; } = new();
    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;
}
