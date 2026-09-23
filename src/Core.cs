using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPeek;

public sealed record LimitWindow(string Name, double UsedPercent, long? ResetAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
    public string ResetText(DateTimeOffset now)
    {
        if (ResetAt is null) return "리셋 시간 정보 없음";
        DateTimeOffset reset;
        try { reset = DateTimeOffset.FromUnixTimeSeconds(ResetAt.Value); }
        catch (ArgumentOutOfRangeException) { return "리셋 시간 정보 없음"; }
        var left = reset - now;
        if (left <= TimeSpan.Zero) return "리셋 시각 경과 · 새로고침 필요";
        if (left.TotalDays >= 1) return $"리셋까지 {(int)left.TotalDays}일 {left.Hours}시간";
        if (left.TotalHours >= 1) return $"리셋까지 {(int)left.TotalHours}시간 {left.Minutes}분";
        return $"리셋까지 {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분";
    }
}
public sealed record UsageSnapshot(List<LimitWindow> Windows, string? Plan, DateTimeOffset FetchedAt);
public static class UsageParser
{
    public static UsageSnapshot Parse(JsonElement root, DateTimeOffset now)
    {
        var windows = new List<LimitWindow>();
        string? plan = null;
        var buckets = new List<(string, JsonElement)>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
            buckets.AddRange(map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => (p.Name, p.Value)));
        if (buckets.Count == 0 && root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            buckets.Add(("codex", legacy));
        foreach (var (key, bucket) in buckets)
        {
            plan ??= Text(bucket, "planType");
            var bucketName = Text(bucket, "limitName") ?? key;
            foreach (var field in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(field, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("usedPercent", out var used) || (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)) || !double.IsFinite(percent)) continue;
                int? minutes = w.TryGetProperty("windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var mins) ? mins : null;
                string name = minutes switch { 10080 => "주간 한도", 1440 => "일간 한도", > 0 when minutes % 60 == 0 => $"{minutes / 60}시간 한도", > 0 => $"{minutes}분 한도", _ => field == "primary" ? "기본 한도" : "추가 한도" };
                if (buckets.Count > 1) name = $"{bucketName} · {name}";
                long? reset = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds) ? seconds : null;
                windows.Add(new LimitWindow(name, percent, reset));
            }
        }
        return new UsageSnapshot(windows, plan, now);
    }
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class UpdateSchedule
{
    public int IntervalMinutes { get; private set; } = 30;
    public DateTimeOffset? LastSuccess { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public void Configure(int minutes, DateTimeOffset now)
    {
        IntervalMinutes = Math.Clamp(minutes, 1, 1440);
        NextAttempt = (LastSuccess ?? now).AddMinutes(IntervalMinutes);
    }
    public void Succeeded(DateTimeOffset now) { LastSuccess = now; NextAttempt = now.AddMinutes(IntervalMinutes); }
    public void Failed(DateTimeOffset now) { NextAttempt = now.AddMinutes(IntervalMinutes); }
    public bool IsDue(DateTimeOffset now) => now >= NextAttempt;
}

public sealed class UserSettings
{
    public int IntervalMinutes { get; set; } = 30;
    public int Transparency { get; set; } = 15;
    public double Width { get; set; } = 270;
    public double Height { get; set; } = 160;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Pinned { get; set; }
    public bool StartWithWindows { get; set; }
    public string? CodexPath { get; set; }
    public bool ShowServices { get; set; } = true;
    public void Normalize()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 1, 1440);
        Transparency = Math.Clamp(Transparency, 0, 80);
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 220, 2000) : 270;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 80, 1600) : 160;
        if (Left is double x && !double.IsFinite(x)) Left = null;
        if (Top is double y && !double.IsFinite(y)) Top = null;
    }
}
public static class SettingsStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPeek");
    public static UserSettings Load()
    {
        try { var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new(); s.Normalize(); return s; }
        catch { return new(); }
    }
    public static void Save(UserSettings settings)
    {
        settings.Normalize(); Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}
public sealed class CodexClient
{
    public static string FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured) && Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return configured;
            throw new InvalidOperationException("설정에서 Codex 실행 파일 경로를 확인하세요.");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new List<string> { Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe") };
        paths.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), "codex.exe")));
        return paths.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Codex를 찾을 수 없습니다. 설정에서 codex.exe를 선택하세요.");
    }
    public async Task<UsageSnapshot> ReadAsync(string? path, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new ProcessStartInfo(FindExecutable(path)) {
            Arguments = "app-server", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        } };
        process.Start();
        // Drain stderr without retaining diagnostics, account identifiers, or credentials.
        var drain = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync(timeout.Token) is not null) { } } catch { } });
        try
        {
            async Task<JsonElement> Request(int id, string method, object? args = null)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = args }).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is string line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var rid) || (rid.ValueKind != JsonValueKind.Number || !rid.TryGetInt32(out var value)) || value != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex 조회에 실패했습니다. 로그인 상태와 네트워크를 확인하세요.");
                    return root.GetProperty("result").Clone();
                }
                throw new InvalidOperationException("Codex 연결이 종료되었습니다. 다시 새로고침해 주세요.");
            }
            await Request(1, "initialize", new { clientInfo = new { name = "codex_peek", version = "1.0.0" } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var account = await Request(2, "account/read", new { refreshToken = false });
            if (!account.TryGetProperty("account", out var a) || a.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException("Codex에 먼저 ChatGPT 계정으로 로그인해 주세요.");
            if (a.TryGetProperty("type", out var type) && type.GetString() == "apiKey")
                throw new InvalidOperationException("구독 한도를 보려면 Codex에 ChatGPT 계정으로 로그인하세요.");
            return UsageParser.Parse(await Request(3, "account/rateLimits/read"), DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("30초 동안 응답이 없습니다. 네트워크를 확인한 뒤 다시 시도하세요."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            timeout.Cancel();
            await drain;
        }
    }
}
