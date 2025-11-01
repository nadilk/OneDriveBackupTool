using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using OneDriveBackupTool.Models;

namespace OneDriveBackupTool.Services;

public class BackupService
{
    public async Task Run(RootConfig config)
    {
        var tasks = config.BackupJobs.Select(job => RunBackupJobAsync(job));
        await Task.WhenAll(tasks);
    }

    private async Task RunBackupJobAsync(BackupJobConfig job)
    {
        Logger.Log($"Starting backup for account: {job.AccountName}");
        try
        {
            string accessToken = await GetAccessTokenAsync(job);

            var (remoteFiles, remoteDirsSet) = await ListOneDriveFilesAsync(job, accessToken);

            var metadataPath = Path.Combine(job.LocalTargetDirectory, ".onedrive-backup-metadata.json");
            var metadata = await LoadMetadataAsync(metadataPath);

            var remoteFileSet = remoteFiles.Select(f => f.FileName).ToHashSet();
            // Ensure root directory is included
            remoteDirsSet.Add(job.OneDriveDirectory);


            // Delete local files not present in OneDrive
            DeleteLocalFilesNotInRemote(job.LocalTargetDirectory, remoteFileSet);
            // Delete local directories not present in OneDrive
            DeleteLocalDirectoriesNotInRemote(job.LocalTargetDirectory, remoteDirsSet);
            // Create any missing local directories for all OneDrive directories
            EnsureLocalDirectoriesExist(job.LocalTargetDirectory, remoteDirsSet);

            int maxConcurrency = 4;
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var downloadTasks = remoteFiles.Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var relPath = file.FileName.Replace("\\", "/");
                    if (metadata.Files.TryGetValue(relPath, out var meta) && meta.ETag == file.ETag && File.Exists(Path.Combine(job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'))))
                    {
                        // Skip unchanged file
                        return;
                    }
                    await DownloadFileAsync(job, file, accessToken);
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
            Logger.Log($"Backup completed for account: {job.AccountName}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in backup for {job.AccountName}: {ex.Message}");
        }
    }

    private async Task<string> GetAccessTokenAsync(BackupJobConfig job)
    {
        using var http = new HttpClient();
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_token.srf")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", job.ClientId),
                new KeyValuePair<string, string>("redirect_uri", "https://login.microsoftonline.com/common/oauth2/nativeclient"),
                new KeyValuePair<string, string>("client_secret", job.ClientSecret),
                new KeyValuePair<string, string>("refresh_token", job.RefreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
            })
        };
        var tokenResp = await http.SendAsync(tokenReq);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed: {tokenJson}");
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;
        return root.GetProperty("access_token").GetString() ?? throw new Exception("No access_token in response");
    }

    private async Task<(List<OneDriveFile> Files, HashSet<string> Dirs)> ListOneDriveFilesAsync(BackupJobConfig job, string accessToken)
    {
        var files = new List<OneDriveFile>();
        var oneDriveDirs = new HashSet<string>();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        string baseUrl = "https://graph.microsoft.com/v1.0/me/drive/root";
        string path = string.IsNullOrWhiteSpace(job.OneDriveDirectory) || job.OneDriveDirectory == "/" ? "" : $":{job.OneDriveDirectory}:";
        string url = $"{baseUrl}{path}/children?$top=999";

        Logger.Log($"Listing OneDrive files in directory: {job.OneDriveDirectory}");
        await ListFilesRecursiveAsync(http, url, files, oneDriveDirs, job.OneDriveDirectory, job);
        Logger.Log($"Total files listed: {files.Count}");
        return (files, oneDriveDirs);
    }

    private async Task ListFilesRecursiveAsync(HttpClient http, string url, List<OneDriveFile> files, HashSet<string> oneDriveDirs, string parentPath, BackupJobConfig job)
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
                if (job.Excluded.Any(pattern => relPath.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Log($"Excluded (file/folder): {relPath}");
                    continue;
                }

                if (isFolder)
                {
                    // Track OneDrive directory only
                    oneDriveDirs.Add(relPath);
                    string folderUrl = $"https://graph.microsoft.com/v1.0/me/drive/items/{id}/children?$top=999";
                    await ListFilesRecursiveAsync(http, folderUrl, files, oneDriveDirs, relPath, job);
                }
                else
                {
                    files.Add(new OneDriveFile
                    {
                        FileName = relPath,
                        Size = item.GetProperty("size").GetInt64(),
                        LastModified = lastModified,
                        ETag = eTag
                    });
                    Logger.Log($"OneDrive file: {relPath} (ETag: {eTag})");
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

    private void EnsureLocalDirectoriesExist(string localRoot, HashSet<string> remoteDirSet)
    {
        foreach (var relPath in remoteDirSet)
        {
            var localDir = Path.Combine(localRoot, relPath.TrimStart('/', '\\'));
            if (!Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
                Logger.Log($"Created local directory for OneDrive folder: {relPath}");
            }
        }
    }

    private async Task DownloadFileAsync(BackupJobConfig job, OneDriveFile file, string accessToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        string url = $"https://graph.microsoft.com/v1.0/me/drive/root:/{file.FileName}:/content";
        string localPath = Path.Combine(job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Log($"Failed to download {file.FileName}: {await resp.Content.ReadAsStringAsync()}");
            return;
        }
        using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs);
        Logger.Log($"Downloaded: {file.FileName}");
    }

    private async Task<BackupMetadata> LoadMetadataAsync(string metadataPath)
    {
        if (!File.Exists(metadataPath))
            return new BackupMetadata();
        var json = await File.ReadAllTextAsync(metadataPath);
        return JsonSerializer.Deserialize<BackupMetadata>(json) ?? new BackupMetadata();
    }

    private async Task SaveMetadataAsync(string metadataPath, BackupMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(metadataPath, json);
    }

    private void DeleteLocalFilesNotInRemote(string localRoot, HashSet<string> remoteFileSet)
    {
        foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(localRoot, file).Replace("\\", "/");
            if (relPath == ".onedrive-backup-metadata.json")
                continue;

            var oneDriveFilePath = '/' + relPath;

            if (!remoteFileSet.Contains(oneDriveFilePath))
            {
                try
                {
                    File.Delete(file);
                    Logger.Log($"Deleted local file not in OneDrive: {relPath}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete {relPath}: {ex.Message}");
                }
            }
        }
    }

    private void DeleteLocalDirectoriesNotInRemote(string localRoot, HashSet<string> remoteDirSet)
    {
        foreach (var dir in Directory.EnumerateDirectories(localRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            var relPath = Path.GetRelativePath(localRoot, dir).Replace("\\", "/");
           
            if (!remoteDirSet.Contains("/" + relPath))
            {
                try
                {
                    Directory.Delete(dir, true);
                    Logger.Log($"Deleted local directory not in OneDrive: {relPath}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete directory {relPath}: {ex.Message}");
                }
            }
        }
    }
}
