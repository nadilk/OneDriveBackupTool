namespace OneDriveBackupTool.Commands;

public interface ICommand
{
    Task ExecuteAsync(string[] args);
}
