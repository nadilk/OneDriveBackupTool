using System.Net.Http;
using System.Text.Json;
using System.Web;
using OneDriveBackupTool.Configuration;
using OneDriveBackupTool.Utils; // Added for Logger

namespace OneDriveBackupTool.Services;

public static class OneDriveAuthService
{
    private const string AuthEndpoint = "https://login.live.com/oauth20_authorize.srf";
    private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
    private const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";
    private static readonly string[] Scopes = new[] { "offline_access", "files.readwrite" };

    public static async Task AuthorizeAndCreateConfigAsync(string configPath)
    {
        Console.WriteLine("Enter a name for this OneDrive account (e.g., Personal, Work):");
        var accountName = Console.ReadLine() ?? "OneDriveAccount";

        Console.WriteLine("Enter your Microsoft App Client ID:");
        var clientId = Console.ReadLine() ?? string.Empty;

        Console.WriteLine("Enter your Microsoft App Client Secret (or leave blank if not required):");
        var clientSecret = Console.ReadLine() ?? string.Empty;

        // Build the authorization URL
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = clientId;
        query["scope"] = string.Join(" ", Scopes);
        query["response_type"] = "code";
        query["redirect_uri"] = RedirectUri;
        var authUrl = $"{AuthEndpoint}?{query}";

        Console.WriteLine("\nOpen the following URL in your browser and authorize the app:");
        Console.WriteLine(authUrl);
        Console.WriteLine("\nAfter authorizing, you will be redirected to a URL. Copy the 'code' parameter from that URL and paste it below.");
        Console.Write("Authorization code: ");
        var code = Console.ReadLine() ?? string.Empty;

        // Exchange code for tokens
        using var http = new HttpClient();
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new[]{
                 new KeyValuePair<string, string>("client_id", clientId),
                 new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                 new KeyValuePair<string, string>("client_secret", clientSecret),
                 new KeyValuePair<string, string>("code", code),
                 new KeyValuePair<string, string>("grant_type", "authorization_code"),
             })
        };
        var tokenResp = await http.SendAsync(tokenReq);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Token request failed: {tokenJson}");
            return;
        }
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;
        var refreshToken = root.GetProperty("refresh_token").GetString() ?? string.Empty;

        Console.WriteLine("Authorization successful!");

        Console.WriteLine("Enter OneDrive source directory to back up (e.g., /Documents or leave blank for root):");
        var oneDriveDir = Console.ReadLine() ?? string.Empty;

        Console.WriteLine("Enter local target directory to save files:");
        var localTargetDir = Console.ReadLine() ?? string.Empty;

        Console.WriteLine("Enter backup interval in minutes (default60):");
        var intervalStr = Console.ReadLine();
        int interval = 60;
        int.TryParse(intervalStr, out interval);

        var job = new BackupJobConfig
        {
            AccountName = accountName,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RefreshToken = refreshToken,
            OneDriveDirectory = oneDriveDir,
            LocalTargetDirectory = localTargetDir,
            BackupIntervalMinutes = interval > 0 ? interval : 60
        };

        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));
        Logger.Log($"Configuration for '{accountName}' saved to {configPath}");
    }
}
