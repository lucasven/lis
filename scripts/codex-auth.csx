#!/usr/bin/env dotnet-script
// Codex OAuth PKCE login script
// Usage: dotnet script scripts/codex-auth.csx
//   — or — dotnet script scripts/codex-auth.csx --output /path/to/auth.json

#r "nuget: System.Text.Json, 9.0.0"

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

const string CLIENT_ID    = "app_EMoamEEZ73f0CkXaXp7hrann";
const string AUTHORIZE_URL = "https://auth.openai.com/oauth/authorize";
const string TOKEN_URL    = "https://auth.openai.com/oauth/token";
const string REDIRECT_URI = "http://localhost:1455/auth/callback";
const string SCOPE        = "openid profile email offline_access";

// Parse --output flag
string outputPath = Path.Combine(Directory.GetCurrentDirectory(), "auth.json");
for (int i = 0; i < Args.Count; i++)
{
    if (Args[i] is "--output" or "-o" && i + 1 < Args.Count)
        outputPath = Args[i + 1];
}

// Generate PKCE
byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
string verifier = Base64UrlEncode(verifierBytes);
byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
string challenge = Base64UrlEncode(challengeHash);
string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

// Build auth URL
string authUrl = AUTHORIZE_URL
    + "?response_type=code"
    + "&client_id=" + CLIENT_ID
    + "&redirect_uri=" + Uri.EscapeDataString(REDIRECT_URI)
    + "&scope=openid+profile+email+offline_access"
    + "&code_challenge=" + challenge
    + "&code_challenge_method=S256"
    + "&state=" + state
    + "&id_token_add_organizations=true"
    + "&codex_cli_simplified_flow=true"
    + "&originator=pi"
    + "&prompt=login";

Console.WriteLine();
Console.WriteLine("🔐 Codex OAuth Login");
Console.WriteLine("====================");
Console.WriteLine();
Console.WriteLine("Open this URL in your browser:");
Console.WriteLine();
Console.WriteLine(authUrl);
Console.WriteLine();
Console.WriteLine("Waiting for callback on http://localhost:1455/auth/callback ...");

// Start local callback server
string code = null;
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:1455/");
listener.Start();

try
{
    // Try to open browser automatically
    try
    {
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", authUrl);
        else if (OperatingSystem.IsLinux())
            System.Diagnostics.Process.Start("xdg-open", authUrl);
    }
    catch { /* Manual open fallback */ }

    while (code == null)
    {
        var ctx = await listener.GetContextAsync();
        var req = ctx.Request;
        var resp = ctx.Response;

        if (req.Url?.AbsolutePath != "/auth/callback")
        {
            resp.StatusCode = 404;
            resp.Close();
            continue;
        }

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        string incomingState = query["state"];
        string incomingCode = query["code"];

        if (incomingState != state)
        {
            byte[] errorBytes = Encoding.UTF8.GetBytes("<html><body><h2>State mismatch. Try again.</h2></body></html>");
            resp.ContentType = "text/html";
            resp.StatusCode = 400;
            resp.ContentLength64 = errorBytes.Length;
            await resp.OutputStream.WriteAsync(errorBytes);
            resp.Close();
            continue;
        }

        if (incomingCode == null)
        {
            string error = query["error"] ?? "unknown";
            string desc = query["error_description"] ?? "";
            byte[] errorBytes = Encoding.UTF8.GetBytes($"<html><body><h2>Auth error: {error}</h2><p>{desc}</p></body></html>");
            resp.ContentType = "text/html";
            resp.StatusCode = 400;
            resp.ContentLength64 = errorBytes.Length;
            await resp.OutputStream.WriteAsync(errorBytes);
            resp.Close();
            Console.WriteLine($"❌ Auth error: {error} — {desc}");
            return 1;
        }

        code = incomingCode;

        byte[] okBytes = Encoding.UTF8.GetBytes("<html><body><h2>✅ Authenticated! You can close this window.</h2></body></html>");
        resp.ContentType = "text/html";
        resp.StatusCode = 200;
        resp.ContentLength64 = okBytes.Length;
        await resp.OutputStream.WriteAsync(okBytes);
        resp.Close();
    }
}
finally
{
    listener.Stop();
}

Console.WriteLine("✅ Authorization code received. Exchanging for tokens...");

// Exchange code for tokens
var http = new HttpClient();
var form = new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code",
    ["client_id"] = CLIENT_ID,
    ["code"] = code,
    ["code_verifier"] = verifier,
    ["redirect_uri"] = REDIRECT_URI
};

var content = new FormUrlEncodedContent(form);
var tokenResp = await http.PostAsync(TOKEN_URL, content);
string body = await tokenResp.Content.ReadAsStringAsync();

if (!tokenResp.IsSuccessStatusCode)
{
    Console.WriteLine($"❌ Token exchange failed ({(int)tokenResp.StatusCode}): {body}");
    return 1;
}

var json = JsonNode.Parse(body);
string accessToken = json["access_token"]?.GetValue<string>();
string refreshToken = json["refresh_token"]?.GetValue<string>();
int? expiresIn = json["expires_in"]?.GetValue<int>();

if (accessToken == null || refreshToken == null)
{
    Console.WriteLine("❌ Token response missing required fields");
    return 1;
}

// Extract account ID from JWT
string[] jwtParts = accessToken.Split('.');
string payload = jwtParts[1].Replace('-', '+').Replace('_', '/');
switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
var claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
string accountId = claims?["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>() ?? "unknown";

// Write auth.json
JsonObject root;
if (File.Exists(outputPath))
{
    try { root = JsonNode.Parse(File.ReadAllText(outputPath))?.AsObject() ?? new JsonObject(); }
    catch { root = new JsonObject(); }
}
else
{
    root = new JsonObject();
}

root["codex"] = new JsonObject
{
    ["access_token"] = accessToken,
    ["refresh_token"] = refreshToken,
    ["persisted_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
};

string jsonOut = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outputPath, jsonOut);

Console.WriteLine();
Console.WriteLine("✅ Codex authenticated successfully!");
Console.WriteLine($"   Account: {accountId}");
Console.WriteLine($"   Expires: {expiresIn}s");
Console.WriteLine($"   Saved:   {outputPath}");
Console.WriteLine();
Console.WriteLine("Copy auth.json to your Lis deployment directory if running remotely.");

return 0;

static string Base64UrlEncode(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
