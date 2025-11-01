namespace OneDriveBackupTool.Configuration;

public class BackupJobConfig
{
 public string AccountName { get; set; } = string.Empty;
 public string ClientId { get; set; } = string.Empty;
 public string ClientSecret { get; set; } = string.Empty;
 public string RefreshToken { get; set; } = string.Empty;
 public string OneDriveDirectory { get; set; } = string.Empty;
 public string LocalTargetDirectory { get; set; } = string.Empty;
 public int BackupIntervalMinutes { get; set; } = 60;
 public List<string> Excluded { get; set; } = new();
}

public class RootConfig
{
 public List<BackupJobConfig> BackupJobs { get; set; } = new();
}
