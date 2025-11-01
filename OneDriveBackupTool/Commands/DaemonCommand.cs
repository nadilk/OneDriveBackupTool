using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Services;
using OneDriveBackupTool.Utils;

namespace OneDriveBackupTool.Commands;

public class DaemonCommand : ICommand
{
    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log("Error: You must provide a config folder path.");
            Logger.Log("Usage: daemon <configFolder>");
            return;
        }

        string configFolder = args[0];
        var daemonService = new DaemonService();
        await daemonService.Run(configFolder);
    }
}
