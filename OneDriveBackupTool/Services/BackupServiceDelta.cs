using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Models;
using OneDriveBackupTool.Utils;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;

namespace OneDriveBackupTool.Services;

public class BackupServiceDelta : BackupServiceBase
{
    public BackupServiceDelta(BackupJobConfig job) : base(job) { }

    public override async Task Run()
    {
        Log($"Starting delta backup for account: {_job.AccountName}");
        try
        {
            string accessToken = await GetAccessTokenAsync();
            var metadataPath = Path.Combine(_job.LocalTargetDirectory, ".onedrive-backup-metadata.json");
            var metadata = await LoadMetadataAsync(metadataPath);

            // Collect all changes in one update object
            var (update, nextLink) = await GetDeltaUpdate(accessToken, metadata);

            // Process all changes
            await ProcessOneDriveUpdate(update, accessToken, metadata);

            await UpdateAndSaveMetadata(metadata, metadataPath, nextLink);

            Log($"Delta backup completed for account: {_job.AccountName}");
        }
        catch (Exception ex)
        {
            Log($"Error in delta backup for {_job.AccountName}: {ex.Message}");
        }
    }

    private async Task UpdateAndSaveMetadata(BackupMetadata metadata, string metadataPath, string nextLink)
    {
        metadata.LastBackupTime = DateTime.UtcNow;
        metadata.TotalFilesBackedUp = metadata.Files.Count;
        metadata.DeltaLink = nextLink;
        await SaveMetadataAsync(metadataPath, metadata);
    }

    private async Task<string> GetInitialDeltaLink(string accessToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        string baseUrl = "https://graph.microsoft.com/v1.0/me/drive/root";
        string path = string.IsNullOrWhiteSpace(_job.OneDriveDirectory) || _job.OneDriveDirectory == "/" ? "" : $":{_job.OneDriveDirectory}:/";
        string url = $"{baseUrl}{path}delta";

        return url;
    }

    private string GetRelativePath(JsonElement item)
    {
        if (item.TryGetProperty("parentReference", out var parentRef) &&
        parentRef.TryGetProperty("path", out var parentPathProp) &&
        item.TryGetProperty("name", out var nameProp))
        {
            var parentPath = parentPathProp.GetString() ?? "";
            var name = nameProp.GetString() ?? "";
            // parentPath is like /drive/root:/folder/subfolder
            var rootPrefix = "/drive/root:";
            if (parentPath.StartsWith(rootPrefix))
                parentPath = parentPath.Substring(rootPrefix.Length);
            var relPath = Path.Combine(parentPath, name).Replace("\\", "/");
            return relPath;
        }
        return null;
    }

    private OneDriveUpdate CollectOneDriveUpdates(JsonElement root, BackupMetadata metadata)
    {
        var update = new OneDriveUpdate();
        if (root.TryGetProperty("value", out var value))
        {
            foreach (var item in value.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                if (item.TryGetProperty("deleted", out _))
                {
                    // File deletion
                    if (metadata.Files.Any(f => f.Value.Id == id))
                        update.DeletedFileIds.Add(id);
                    // Folder deletion
                    if (metadata.Folders != null && metadata.Folders.Any(f => f.Value.Id == id))
                        update.DeletedFolderIds.Add(id);
                    continue;
                }
                var relPath = GetRelativePath(item);
                if (string.IsNullOrEmpty(relPath)) continue;

                if (item.TryGetProperty("folder", out _))
                {
                    update.Folders.Add(new OneDriveFolder { Id = id, Path = relPath });
                }
                else
                {
                    update.Files.Add(new OneDriveFile
                    {
                        Id = id,
                        FileName = relPath,
                        Size = item.GetProperty("size").GetInt64(),
                        LastModified = item.GetProperty("lastModifiedDateTime").GetDateTime(),
                        ETag = item.GetProperty("eTag").GetString() ?? string.Empty,
                        CTag = item.GetProperty("cTag").GetString() ?? string.Empty
                    });
                }
            }
        }
        return update;
    }

    private async Task<(OneDriveUpdate update, string nextLink)> GetDeltaUpdate(string accessToken, BackupMetadata metadata)
    {
        string deltaLink = metadata.DeltaLink ?? await GetInitialDeltaLink(accessToken);
        bool hasMore = true;
        string nextLink = deltaLink;
        var update = new OneDriveUpdate();

        while (hasMore && !string.IsNullOrEmpty(nextLink))
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await http.GetAsync(nextLink);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Delta query failed: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pageUpdate = CollectOneDriveUpdates(root, metadata);
            update.Files.AddRange(pageUpdate.Files);
            update.Folders.AddRange(pageUpdate.Folders);
            update.DeletedFileIds.AddRange(pageUpdate.DeletedFileIds);
            update.DeletedFolderIds.AddRange(pageUpdate.DeletedFolderIds);

            if (root.TryGetProperty("@odata.nextLink", out var nextLinkProp))
            {
                nextLink = nextLinkProp.GetString() ?? string.Empty;
                hasMore = true;
            }
            else if (root.TryGetProperty("@odata.deltaLink", out var deltaLinkProp))
            {
                nextLink = deltaLinkProp.GetString() ?? string.Empty;
                hasMore = false;
            }
            else
            {
                hasMore = false;
            }
        }
        return (update, nextLink);
    }

    private bool IsPathExcluded(string path)
    {
        return _job.Excluded.Any(pattern => path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private string GetLocalPath(OneDriveFile file)
    {
        return Path.Combine(_job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'));
    }

    private string GetLocalPath(OneDriveFolder folder)
    {
        return Path.Combine(_job.LocalTargetDirectory, folder.Path.TrimStart('/', '\\'));
    }

    private async Task ProcessOneDriveUpdate(OneDriveUpdate update, string accessToken, BackupMetadata metadata)
    {
        //1. Handle file moves/renames/updates first
        using var semaphore = new SemaphoreSlim(_job.MaxConcurrency);
        var downloadTasks = update.Files.Select((Func<OneDriveFile, Task>)(async file =>
        {
            await semaphore.WaitAsync();
            try
            {
                var localFile = metadata.Files.FirstOrDefault(kvp => kvp.Value.Id == file.Id).Value;

                // Exclusion check for new file path
                file.Excluded = IsPathExcluded(file.FileName);
                // Add to metadata anyway (even if excluded)
                metadata.Files[file.FileName] = file;

                if (localFile != null)
                {
                    var oldLocalPath = GetLocalPath(localFile);
                    var newLocalPath = GetLocalPath(file);

                    if (localFile.FileName != file.FileName)
                    {
                        metadata.Files.Remove(localFile.FileName);
                    }

                    if (!localFile.Excluded)
                    {
                        if (file.Excluded)
                        {
                            // If the new path is excluded, delete the old file
                            if (File.Exists(oldLocalPath))
                            {
                                File.Delete(oldLocalPath);
                                Log($"Deleted local file (moved, excluded): {localFile.FileName} -> {file.FileName}");
                                return;
                            }

                        }
                        else
                        {
                            var newLocalDir = Path.GetDirectoryName(newLocalPath);

                            if (!string.IsNullOrEmpty(newLocalDir) && !Directory.Exists(newLocalDir))
                            {
                                Directory.CreateDirectory(newLocalDir);
                            }

                            if (File.Exists(oldLocalPath))
                            {
                                File.Move(oldLocalPath, newLocalPath, overwrite: true);
                                Log($"Moved local file: {localFile.FileName} -> {file.FileName}");
                            }
                        }
                    }
                }
              

                if (file.Excluded)
                {
                    Log($"Skipped excluded file: {file.FileName}");
                }
                else
                {
                    // Use cTag for content comparison
                    if (localFile != null && localFile.CTag == file.CTag &&
                        File.Exists(Path.Combine(_job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'))))
                    {
                        // Skip unchanged file
                        return;
                    }
                    await DownloadFileAsync(file, accessToken);
                }

            }
            finally
            {
                semaphore.Release();
            }
        }));
        await Task.WhenAll(downloadTasks);

        //2. Handle deleted files
        foreach (var fileId in update.DeletedFileIds)
        {
            var localFile = metadata.Files.FirstOrDefault(f => f.Value.Id == fileId).Value;
            if (localFile != null && !localFile.Excluded)
            {
                var localPath = GetLocalPath(localFile);
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                    Log($"Deleted local file: {localFile.FileName}");
                }
                metadata.Files.Remove(localFile.FileName);
            }
        }

        //3. Handle folder changes (add/move/rename)
        foreach (var folder in update.Folders)
        {
            var oldFolder = metadata.Folders.FirstOrDefault(f => f.Value.Id == folder.Id).Value;

            folder.Excluded = IsPathExcluded(folder.Path);
            metadata.Folders[folder.Path] = folder;


            if (oldFolder != null)
            {
                var oldLocalPath = GetLocalPath(oldFolder);
                var newLocalPath = GetLocalPath(folder);
             
                if(oldFolder.Path != folder.Path)
                {
                    metadata.Folders.Remove(oldFolder.Path);
                }

                if (!oldFolder.Excluded)
                {
                    if (folder.Excluded)
                    {
                        // If the new folder is excluded, delete the old folder
                        if (Directory.Exists(oldLocalPath))
                        {
                            Directory.Delete(oldLocalPath, true);
                            Log($"Deleted local folder (moved, excluded): {oldFolder.Path} -> {folder.Path}");
                            return;
                        }
                    }
                    else if (Directory.Exists(oldLocalPath) && !Directory.Exists(newLocalPath))
                    {
                        Directory.Move(oldLocalPath, newLocalPath);
                        Log($"Moved local folder: {oldFolder.Path} -> {folder.Path}");
                    }
                }
            }

            if (folder.Excluded)
            {
                Log($"Skipped excluded folder: {folder.Path}");
            }
            else
            {
                var localPath = GetLocalPath(folder);
                if (!Directory.Exists(localPath))
                {
                    Directory.CreateDirectory(localPath);
                    Log($"Created local folder: {folder.Path}");
                }
            }

            
        }

        //4. Handle deleted folders
        foreach (var folderId in update.DeletedFolderIds)
        {
            var folderEntry = metadata.Folders.FirstOrDefault(f => f.Value.Id == folderId);
            var replacingFolder = metadata.Folders.FirstOrDefault(f => f.Value.Id != folderId && f.Value.Path == folderEntry.Value.Path);

            if (!string.IsNullOrEmpty(folderEntry.Key))
            {
                var localPath = Path.Combine(_job.LocalTargetDirectory, folderEntry.Key.TrimStart('/', '\\'));
                if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, true);
                    Log($"Deleted local folder: {folderEntry.Key}");
                }
                metadata.Folders.Remove(folderEntry.Key);
            }
        }
    }
}
