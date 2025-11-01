using Microsoft.Extensions.Configuration;
using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Services;
using OneDriveBackupTool.Utils;

namespace OneDriveBackupTool.Commands;

public class SyncCommand : ICommand
{
    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log("Error: You must provide a config file path.");
            Logger.Log("Usage: sync <configPath>");
            return;
        }
        string configPath = args[0];
        if (!File.Exists(configPath))
        {
            Logger.Log($"Error: Config file '{configPath}' does not exist.");
            return;
        }
        var config = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile(configPath, optional: false, reloadOnChange: true)
        .Build();

        if (config == null)
        {
            Logger.Log($"Error: Configuration file '{configPath}' could not be loaded.");
            return;
        }

        BackupJobConfig? jobConfig = null;
        try
        {
            jobConfig = config.Get<BackupJobConfig>();
            if (jobConfig == null)
            {
                Logger.Log("Error: Deserialized BackupJobConfig is null. Check your config file structure.");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error: Failed to load or parse config file '{configPath}': {ex.Message}");
            return;
        }
        Logger.Log("Starting OneDrive Backup Tool");
        var backupService = new BackupService(jobConfig);
        await backupService.Run();
        Logger.Log("Backup completed.");
    }
}
