using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexPeek;

public sealed record ClaudeSnapshot(List<LimitWindow> Windows, DateTimeOffset ReceivedAt);
public sealed record MailItem(string Subject, string Sender);
public sealed record MailSnapshot(string Account, int Unread, List<MailItem> Messages, DateTimeOffset FetchedAt);

public static class ClaudeBridge
{
    public static string CachePath => Path.Combine(SettingsStore.Folder, "claude-usage.json");
    public static string ErrorPath => Path.Combine(SettingsStore.Folder, "claude-bridge-error.txt");
    public static void ReportFailure(string code)
    {
        try { Directory.CreateDirectory(SettingsStore.Folder); File.WriteAllText(ErrorPath, code); } catch { }
    }
    private static string ConfigPath => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");
    public static string Command(string executable)
    {
        // One command works under both Git Bash and PowerShell, including paths containing spaces.
        string script = "$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
            "$peekReader = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [System.Text.UTF8Encoding]::new($false), $true); " +
            "$peekReader.ReadToEnd() | & '" + executable.Replace("'", "''") + "' --claude-statusline";
        return "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
    private static string LegacyScript(string executable) => "$OutputEncoding = [System.Text.UTF8Encoding]::new($false); $input | & '" + executable.Replace("'", "''") + "' --claude-statusline";
    private static string LegacyCommand(string executable) => "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(LegacyScript(executable)));

    public static ClaudeSnapshot Parse(JsonElement root, DateTimeOffset receivedAt)
    {
        var windows = new List<LimitWindow>();
        if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, name) in new[] { ("five_hour", "5시간 한도"), ("seven_day", "주간 한도") })
            {
                if (!limits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object ||
                    !w.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number ||
                    !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                long? reset = w.TryGetProperty("resets_at", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) ? epoch : null;
                windows.Add(new LimitWindow(name, percent, reset));
            }
        }
        return new(windows, receivedAt);
    }
    public static void Capture(string json, string? destination = null)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        var data = Parse(document.RootElement, DateTimeOffset.Now);
        // Retain only quota fields: never persist the input's conversation, paths or session ID.
        var file = destination ?? CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(data)); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (destination is null && File.Exists(ErrorPath)) File.Delete(ErrorPath);
    }
    public static ClaudeSnapshot? Read()
    {
        if (!File.Exists(CachePath)) return null;
        return JsonSerializer.Deserialize<ClaudeSnapshot>(File.ReadAllText(CachePath));
    }
    public static bool IsConnected(string executable)
    {
        try { var command = ReadConfig()["statusLine"]?["command"]?.GetValue<string>(); return command == Command(executable) || command == LegacyCommand(executable); }
        catch { return false; }
    }
    public static void UpgradeOwnedConnection(string executable)
    {
        if (IsConnected(executable)) Connect(executable);
    }
    private static JsonObject ReadConfig() => File.Exists(ConfigPath)
        ? JsonNode.Parse(File.ReadAllText(ConfigPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject() : new();
    public static void Connect(string executable)
    {
        var config = ReadConfig();
        if (config["statusLine"] is not null && !IsConnected(executable))
            throw new InvalidOperationException("기존 Claude 상태 표시줄이 있어 자동 변경하지 않았습니다. README의 수동 연결 안내를 확인하세요.");
        if (config["statusLine"]?["command"]?.GetValue<string>() == Command(executable)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".codexpeek-" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".bak");
        var statusLine = config["statusLine"]?.AsObject() ?? new JsonObject();
        statusLine["type"] = "command"; statusLine["command"] = Command(executable);
        if (config["statusLine"] is null) config["statusLine"] = statusLine;
        WriteConfig(config);
        if (File.Exists(CachePath)) File.Delete(CachePath);
        if (File.Exists(ErrorPath)) File.Delete(ErrorPath);
    }
    public static void Disconnect(string executable)
    {
        if (IsConnected(executable)) { var config = ReadConfig(); config.Remove("statusLine"); WriteConfig(config); }
        if (File.Exists(CachePath)) File.Delete(CachePath);
        if (File.Exists(ErrorPath)) File.Delete(ErrorPath);
    }
    private static void WriteConfig(JsonObject config)
    {
        File.WriteAllText(ConfigPath + ".codexpeek.tmp", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(ConfigPath + ".codexpeek.tmp", ConfigPath, true);
    }
}
