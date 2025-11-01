using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OneDriveBackupTool.Models;

namespace OneDriveBackupTool.Services;

public abstract class BackupServiceBase
{
    protected readonly BackupJobConfig _job;

    protected BackupServiceBase(BackupJobConfig job)
    {
        _job = job;
    }

    public static BackupServiceBase Create(BackupJobConfig job)
    {
        return job.SyncMode == BackupJobConfig.SyncModeType.Delta
        ? new BackupServiceDelta(job)
        : new BackupServiceFullListing(job);
    }

    public abstract Task Run();

    protected void Log(string message) => Logger.Log($"[Job-{_job.AccountName}] {message}");

    protected async Task<string> GetAccessTokenAsync()
    {
        using var http = new HttpClient();
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_token.srf")
        {
            Content = new FormUrlEncodedContent(
                new[]{
                    new KeyValuePair<string, string>("client_id", _job.ClientId),
                    new KeyValuePair<string, string>("redirect_uri", "https://login.microsoftonline.com/common/oauth2/nativeclient"),
                    new KeyValuePair<string, string>("client_secret", _job.ClientSecret),
                    new KeyValuePair<string, string>("refresh_token", _job.RefreshToken),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                }
            )
        };
        var tokenResp = await http.SendAsync(tokenReq);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed: {tokenJson}");
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;
        return root.GetProperty("access_token").GetString() ?? throw new Exception("No access_token in response");
    }

    protected async Task<BackupMetadata> LoadMetadataAsync(string metadataPath)
    {
        if (!File.Exists(metadataPath))
            return new BackupMetadata();
        var json = await File.ReadAllTextAsync(metadataPath);
        return JsonSerializer.Deserialize<BackupMetadata>(json) ?? new BackupMetadata();
    }

    protected async Task SaveMetadataAsync(string metadataPath, BackupMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(metadataPath, json);
    }

    protected void EnsureLocalDirectoriesStructure(IEnumerable<string> remoteDirSet)
    {
        foreach (var relPath in remoteDirSet)
        {
            var localDir = Path.Combine(_job.LocalTargetDirectory, relPath.TrimStart('/', '\\'));
            if (!Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
                Log($"Created local directory for OneDrive folder: {relPath}");
            }
        }
    }

    protected async Task DownloadFileAsync(OneDriveFile file, string accessToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        string url = $"https://graph.microsoft.com/v1.0/me/drive/root:/{file.FileName}:/content";
        string localPath = Path.Combine(_job.LocalTargetDirectory, file.FileName.TrimStart('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"Failed to download {file.FileName}: {await resp.Content.ReadAsStringAsync()}");
            return;
        }
        using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs);
        Log($"Downloaded: {file.FileName}");
    }
}
