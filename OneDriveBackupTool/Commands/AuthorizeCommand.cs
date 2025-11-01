using OneDriveBackupTool.Services;
using OneDriveBackupTool.Utils;

namespace OneDriveBackupTool.Commands;

public class AuthorizeCommand : ICommand
{
    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Logger.Log("Error: You must provide a config file path for authorization.");
            Logger.Log("Usage: authorize <configPath>");
            return;
        }
        string configPath = args[0];
        await OneDriveAuthService.AuthorizeAndCreateConfigAsync(configPath);
    }
}
