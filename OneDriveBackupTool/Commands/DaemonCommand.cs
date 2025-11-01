using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Services;
using OneDriveBackupTool.Utils;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;

namespace OneDriveBackupTool.Commands;

public class DaemonCommand : ICommand
{
    // Prevent double-launch of sync jobs per unique job key
    private static readonly ConcurrentDictionary<string, object> RunningJobs = new();

    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log("Error: You must provide a config folder path.");
            Logger.Log("Usage: daemon <configFolder>");
            return;
        }
        string configFolder = args[0];
        if (!Directory.Exists(configFolder))
        {
            Logger.Log($"Error: Config folder '{configFolder}' does not exist.");
            return;
        }
        Logger.Log($"Daemon started. Scanning configs in: {configFolder}");
        var configFiles = Directory.GetFiles(configFolder, "*.json", SearchOption.TopDirectoryOnly);
        var jobSchedules = new List<(BackupJobConfig job, string configPath)>();
        foreach (var configFile in configFiles)
        {
            try
            {
                var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configFile, optional: false, reloadOnChange: false)
                .Build();
                var job = config.Get<BackupJobConfig>();
                if (job != null)
                {
                    jobSchedules.Add((job, configFile));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load config '{configFile}': {ex.Message}");
            }
        }
        if (jobSchedules.Count == 0)
        {
            Logger.Log("No backup jobs found in configs.");
            return;
        }
        Logger.Log($"Found {jobSchedules.Count} backup jobs. Scheduling...");
        var tasks = jobSchedules.Select(js => RunJobLoop(js.job, js.configPath));
        await Task.WhenAll(tasks);
    }

    private async Task RunJobLoop(BackupJobConfig job, string configPath)
    {
        var backupService = new BackupService(job);
        var jobKey = $"{job.AccountName}|{configPath}";
        var interval = TimeSpan.FromMinutes(Math.Max(1, job.BackupIntervalMinutes));
        while (true)
        {
            if (RunningJobs.TryAdd(jobKey, null))
            {
                try
                {
                    Logger.Log($"[Daemon] Running backup for {job.AccountName} from {configPath}");
                    await backupService.Run();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Daemon] Error in backup for {job.AccountName}: {ex.Message}");
                }
                finally
                {
                    RunningJobs.TryRemove(jobKey, out _);
                }
            }
            else
            {
                Logger.Log($"[Daemon] Backup for {job.AccountName} is already running. Skipping this interval.");
            }
            await Task.Delay(interval);
        }
    }
}
