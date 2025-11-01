using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using OneDriveBackupTool.Models;

namespace OneDriveBackupTool.Services;

public class BackupServiceFullListing : BackupServiceBase
{
    public BackupServiceFullListing(BackupJobConfig job) : base(job) { }

    public override async Task Run()
    {
        Log($"Starting backup for account: {_job.AccountName}");
        try
        {
            string accessToken = await GetAccessTokenAsync();

            var (remoteFiles, remoteDirsSet) = await ListOneDriveFilesAsync(accessToken);

            var metadataPath = Path.Combine(_job.LocalTargetDirectory, ".onedrive-backup-metadata.json");
            var metadata = await LoadMetadataAsync(metadataPath);

            var remoteFileSet = remoteFiles.Select(f => f.FileName).ToHashSet();
            // Add root directory
            remoteDirsSet.Add(_job.OneDriveDirectory);


            // Delete local files not present in OneDrive
            DeleteLocalFilesNotInRemote(remoteFileSet, metadata);
            // Delete local directories not present in OneDrive
            DeleteLocalDirectoriesNotInRemote(remoteDirsSet);
            // Create any missing local directories for all OneDrive directories
            EnsureLocalDirectoriesStructure(remoteDirsSet);


            using var semaphore = new SemaphoreSlim(_job.MaxConcurrency);

            var downloadTasks = remoteFiles.Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var relPath = file.FileName.Replace("\\", "/");
                    if (metadata.Files.TryGetValue(relPath, out var meta) && meta.ETag == file.ETag && File.Exists(Path.Combine(_job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'))))
                    {
                        // Skip unchanged file
                        return;
                    }
                    await DownloadFileAsync(file, accessToken);
                    metadata.Files[relPath] = file;
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(downloadTasks);
            metadata.LastBackupTime = DateTime.UtcNow;
            metadata.TotalFilesBackedUp = metadata.Files.Count;
            await SaveMetadataAsync(metadataPath, metadata);
            Log($"Backup completed for account: {_job.AccountName}");
        }
        catch (Exception ex)
        {
            Log($"Error in backup for {_job.AccountName}: {ex.Message}");
        }
    }

    private async Task<(List<OneDriveFile> Files, HashSet<string> Dirs)> ListOneDriveFilesAsync(string accessToken)
    {
        var files = new List<OneDriveFile>();
        var oneDriveDirs = new HashSet<string>();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        string baseUrl = "https://graph.microsoft.com/v1.0/me/drive/root";
        string path = string.IsNullOrWhiteSpace(_job.OneDriveDirectory) || _job.OneDriveDirectory == "/" ? "" : $":{_job.OneDriveDirectory}:";
        string url = $"{baseUrl}{path}/children?$top=999";

        Log($"Listing OneDrive files in directory: {_job.OneDriveDirectory}");
        await ListFilesRecursiveAsync(http, url, files, oneDriveDirs, _job.OneDriveDirectory);
        Log($"Total files listed: {files.Count}");
        return (files, oneDriveDirs);
    }

    private async Task ListFilesRecursiveAsync(HttpClient http, string url, List<OneDriveFile> files, HashSet<string> oneDriveDirs, string parentPath)
    {
        while (!string.IsNullOrEmpty(url))
        {
            var resp = await http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Failed to list files: {json}");
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("value");
            foreach (var item in value.EnumerateArray())
            {
                var isFolder = item.TryGetProperty("folder", out _);
                var name = item.GetProperty("name").GetString() ?? "";
                var id = item.GetProperty("id").GetString() ?? "";
                var lastModified = item.GetProperty("lastModifiedDateTime").GetDateTime();
                var eTag = item.GetProperty("eTag").GetString() ?? string.Empty;
                var relPath = System.IO.Path.Combine(parentPath, name).Replace('\\', '/');

                // Unified exclusion logic
                if (_job.Excluded.Any(pattern => relPath.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    Log($"Excluded (file/folder): {relPath}");
                    continue;
                }

                if (isFolder)
                {
                    // Track OneDrive directory only
                    oneDriveDirs.Add(relPath);
                    string folderUrl = $"https://graph.microsoft.com/v1.0/me/drive/items/{id}/children?$top=999";
                    await ListFilesRecursiveAsync(http, folderUrl, files, oneDriveDirs, relPath);
                }
                else
                {
                    files.Add(new OneDriveFile
                    {
                        Id = id,
                        FileName = relPath,
                        Size = item.GetProperty("size").GetInt64(),
                        LastModified = lastModified,
                        ETag = eTag
                    });
                    Log($"OneDrive file: {relPath} (ETag: {eTag})");
                }
            }
            // Handle pagination
            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp))
            {
                url = nextLinkProp.GetString() ?? string.Empty;
            }
            else
            {
                url = string.Empty;
            }
        }
    }

    private void DeleteLocalFilesNotInRemote(HashSet<string> remoteFileSet, BackupMetadata metadata)
    {
        foreach (var file in Directory.EnumerateFiles(_job.LocalTargetDirectory, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(_job.LocalTargetDirectory, file).Replace("\\", "/");
            if (relPath == ".onedrive-backup-metadata.json")
                continue;

            var oneDriveFilePath = '/' + relPath;

            if (!remoteFileSet.Contains(oneDriveFilePath))
            {
                try
                {
                    File.Delete(file);
                    Log($"Deleted local file not in OneDrive: {relPath}");
                    metadata.Files.Remove(relPath);
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete {relPath}: {ex.Message}");
                }
            }
        }
    }

    private void DeleteLocalDirectoriesNotInRemote(HashSet<string> remoteDirSet)
    {
        foreach (var dir in Directory.EnumerateDirectories(_job.LocalTargetDirectory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            var relPath = Path.GetRelativePath(_job.LocalTargetDirectory, dir).Replace("\\", "/");

            var oneDriveDirPath = '/' + relPath;

            if (!remoteDirSet.Contains(oneDriveDirPath))
            {
                try
                {
                    Directory.Delete(dir, true);
                    Log($"Deleted local directory not in OneDrive: {relPath}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete directory {relPath}: {ex.Message}");
                }
            }
        }
    }
}
