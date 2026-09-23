using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPeek;

public sealed record GmailCredentials(string ClientId, string ClientSecret, string RefreshToken);

// Windows DPAPI ties saved credentials to the current Windows user.
public static class LocalSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows 계정의 연결 정보를 읽거나 저장할 수 없습니다. 다시 연결해 주세요.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}

public sealed class GmailClient
{
    public const string Scope = "https://www.googleapis.com/auth/gmail.metadata";
    private readonly HttpClient http;
    private readonly string credentialPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expiresAt;
    public GmailClient(HttpClient? http = null, string? credentialPath = null)
    {
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        this.credentialPath = credentialPath ?? Path.Combine(SettingsStore.Folder, "gmail-credentials.bin");
    }
    public bool IsConnected => File.Exists(credentialPath);
    public static GmailCredentials ParseClient(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("installed", out var installed) ||
            !installed.TryGetProperty("client_id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidOperationException("Google Cloud에서 데스크톱 앱 유형으로 받은 OAuth JSON을 선택하세요.");
        return new(id.GetString()!, installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() ?? "" : "", "");
    }
    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]))
        .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last().Length > 1 ? g.Last()[1].Replace('+', ' ') : ""));
    public static string ValidateCallback(Uri uri, string state)
    {
        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("state", out var actual) || actual != state) throw new InvalidOperationException("로그인 응답을 확인할 수 없습니다. 다시 연결하세요.");
        if (query.ContainsKey("error")) throw new InvalidOperationException("Google 계정 연결이 취소되었거나 허용되지 않았습니다.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Google 로그인 코드가 없습니다.");
        return code;
    }
    public async Task ConnectAsync(string clientJson, CancellationToken cancellation)
    {
        var credentials = ParseClient(clientJson);
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var token = timeout.Token;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
                string state = Base64Url(RandomNumberGenerator.GetBytes(32));
                var parameters = new Dictionary<string, string> { ["client_id"] = credentials.ClientId, ["redirect_uri"] = redirect,
                    ["response_type"] = "code", ["scope"] = Scope, ["access_type"] = "offline", ["prompt"] = "consent select_account",
                    ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
                string url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                string? code = null;
                while (code is null)
                {
                    using var socket = await listener.AcceptTcpClientAsync(token);
                    using var stream = socket.GetStream();
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var line = await reader.ReadLineAsync(requestTimeout.Token) ?? "";
                    var parts = line.Split(' ');
                    bool valid = parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/?", StringComparison.Ordinal) && parts[1].Length < 16000;
                    Uri? callback = valid ? new Uri(redirect.TrimEnd('/') + parts[1]) : null;
                    valid = callback is not null && ParseQuery(callback.Query).TryGetValue("state", out var actual) && actual == state;
                    var response = Encoding.UTF8.GetBytes(valid ? "Connection received. Return to Codex Peek to check the result." : "Invalid callback.");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, token); await stream.WriteAsync(response, token);
                    if (valid) code = ValidateCallback(callback!, state);
                }
                var result = await TokenAsync(new() { ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.ClientSecret,
                    ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" }, token);
                if (!result.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrEmpty(refresh.GetString()))
                    throw new InvalidOperationException("자동 갱신 권한을 받지 못했습니다. 다시 연결해 주세요.");
                if (!result.TryGetProperty("scope", out var granted) || !(granted.GetString() ?? "").Split(' ').Contains(Scope))
                    throw new InvalidOperationException("메일 목록 조회 권한이 필요합니다. 다시 연결해 주세요.");
                Save(credentials with { RefreshToken = refresh.GetString()! }); SetAccess(result);
            }
            finally { listener.Stop(); }
        }
        finally { gate.Release(); }
    }
    private GmailCredentials Load() => JsonSerializer.Deserialize<GmailCredentials>(LocalSecret.Transform(File.ReadAllBytes(credentialPath), false))!;
    private void Save(GmailCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        File.WriteAllBytes(credentialPath + ".tmp", LocalSecret.Transform(JsonSerializer.SerializeToUtf8Bytes(credentials), true));
        File.Move(credentialPath + ".tmp", credentialPath, true);
    }
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { if (File.Exists(credentialPath)) File.Delete(credentialPath); accessToken = null; }
        finally { gate.Release(); }
    }
    private async Task<JsonElement> TokenAsync(Dictionary<string, string> fields, CancellationToken token)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(fields), token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google 인증이 실패했습니다. 계정을 다시 연결해 주세요.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return doc.RootElement.Clone();
    }
    private void SetAccess(JsonElement result)
    {
        accessToken = result.GetProperty("access_token").GetString();
        expiresAt = DateTimeOffset.Now.AddSeconds(result.GetProperty("expires_in").GetInt32() - 60);
    }
    public async Task<MailSnapshot> ReadAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            if (!IsConnected) throw new InvalidOperationException("Gmail 계정을 연결해 주세요.");
            if (accessToken is null || DateTimeOffset.Now >= expiresAt)
            {
                var credentials = Load();
                SetAccess(await TokenAsync(new() { ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.ClientSecret,
                    ["refresh_token"] = credentials.RefreshToken, ["grant_type"] = "refresh_token" }, token));
            }
            return await FetchMailboxAsync(http, accessToken!, token);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Gmail 연결을 확인해 주세요. 잠시 후 다시 시도합니다."); }
        finally { gate.Release(); }
    }
    public static async Task<MailSnapshot> FetchMailboxAsync(HttpClient http, string access, CancellationToken token)
    {
        async Task<JsonElement> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Gmail 권한과 API 활성화를 확인하거나 다시 연결해 주세요." : "Gmail 조회에 실패했습니다. 잠시 후 새로고침하세요.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return doc.RootElement.Clone();
        }
        var profile = await Get("profile");
        var inbox = await Get("labels/INBOX");
        var list = await Get("messages?labelIds=INBOX&maxResults=3");
        var messages = new List<MailItem>();
        if (list.TryGetProperty("messages", out var items))
            foreach (var item in items.EnumerateArray())
            {
                var message = await Get("messages/" + Uri.EscapeDataString(item.GetProperty("id").GetString()!) + "?format=metadata&metadataHeaders=Subject&metadataHeaders=From");
                string Header(string name) => message.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headers)
                    ? headers.EnumerateArray().Where(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                        .Select(h => h.GetProperty("value").GetString()).FirstOrDefault() ?? "" : "";
                messages.Add(new(string.IsNullOrWhiteSpace(Header("Subject")) ? "(제목 없음)" : Header("Subject"), Header("From")));
            }
        return new(profile.GetProperty("emailAddress").GetString() ?? "Gmail", inbox.GetProperty("messagesUnread").GetInt32(), messages, DateTimeOffset.Now);
    }
}
