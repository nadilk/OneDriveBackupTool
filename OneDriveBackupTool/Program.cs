using Microsoft.Extensions.Configuration;
using OneDriveBackupTool.Commands;
using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Services;
using OneDriveBackupTool.Utils;

if (args.Length == 0)
{
    Logger.Log("Error: You must provide a command and config file path.");
    Logger.Log("Usage: sync <configPath> | authorize <configPath>");
    return 1;
}

string command = args[0].ToLower();
string[] commandArgs = args.Skip(1).ToArray();
ICommand cmd;
switch (command)
{
    case "authorize":
        cmd = new AuthorizeCommand();
        break;
    case "sync":
        cmd = new SyncCommand();
        break;
    default:
        // Default to sync if only config path is provided
        if (args.Length == 1 && File.Exists(args[0]))
        {
            cmd = new SyncCommand();
            commandArgs = args;
        }
        else
        {
            Logger.Log($"Unknown command: {command}");
            Logger.Log("Usage: sync <configPath> | authorize <configPath>");
            return 1;
        }
        break;
}
await cmd.ExecuteAsync(commandArgs);
return 0;
