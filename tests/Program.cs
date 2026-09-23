using System.Text.Json;
using CodexPeek;

if (args.FirstOrDefault() == "app-server")
{
    string mode = Environment.GetEnvironmentVariable("CODEX_PEEK_TEST_MODE") ?? "success";
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var id)) continue;
        int n = id.GetInt32();
        if (mode == "wait") { await Task.Delay(60000); continue; }
        if (n == 1) Console.WriteLine("{\"id\":1,\"result\":{}}");
        if (n == 2) Console.WriteLine(mode == "unauthenticated" ? "{\"id\":2,\"result\":{\"account\":null}}" : "{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\"}}}");
        if (n == 3)
        {
            Console.WriteLine("{\"method\":\"account/updated\",\"params\":{}}");
            Console.WriteLine(mode == "error" ? "{\"id\":3,\"error\":{\"code\":-1,\"message\":\"private diagnostic\"}}" : "{\"id\":3,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":10080,\"resetsAt\":1790561364}}}}");
        }
    }
    return;
}
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
UsageSnapshot Parse(string json) { using var d = JsonDocument.Parse(json); return UsageParser.Parse(d.RootElement, DateTimeOffset.Now); }
var legacy = Parse("""{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1790561364},"secondary":null}}""");
Check(legacy.Windows.Count == 1 && legacy.Windows[0].Name == "주간 한도" && legacy.Windows[0].Remaining == 75, "weekly legacy response");
var map = Parse("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":10080}},"other":{"primary":{"usedPercent":30,"windowDurationMins":60}}}}""");
Check(map.Windows.Count == 3 && map.Windows[0].Remaining == 90 && map.Windows[2].Name.StartsWith("other"), "multiple buckets replace legacy and retain windows");
var nullable = Parse("""{"rateLimits":{"primary":{"usedPercent":4,"windowDurationMins":null,"resetsAt":null},"secondary":{"usedPercent":null}}}""");
Check(nullable.Windows.Count == 1 && nullable.Windows[0].ResetAt is null, "nullable fields are not fabricated");
Check(Parse("{}").Windows.Count == 0, "missing quota is not zero usage");
Check(new LimitWindow("test", 140, null).Remaining == 0 && new LimitWindow("test", -20, null).Remaining == 100, "remaining percentage bounds");
Check(new LimitWindow("test", 20, 0).ResetText(DateTimeOffset.Now).Contains("새로고침"), "expired reset does not fabricate renewed quota");
var t = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
var schedule = new UpdateSchedule(); schedule.Configure(30, t); schedule.Succeeded(t);
Check(!schedule.IsDue(t.AddMinutes(29)) && schedule.IsDue(t.AddMinutes(30)), "default 30 minute deadline");
schedule.Succeeded(t.AddMinutes(10));
Check(schedule.NextAttempt == t.AddMinutes(40), "manual success restarts interval");
schedule.Failed(t.AddMinutes(11));
Check(schedule.LastSuccess == t.AddMinutes(10) && schedule.NextAttempt == t.AddMinutes(41), "failure preserves last success and backs off");
schedule.Configure(1, t.AddMinutes(12)); Check(schedule.IsDue(t.AddMinutes(12)), "shortened interval becomes immediately due");
schedule.Configure(9999, t); Check(schedule.IntervalMinutes == 1440, "interval upper bound");
var settings = new UserSettings { IntervalMinutes = 0, Transparency = 999, Width = double.NaN, Height = 1 };
settings.Normalize(); Check(settings.IntervalMinutes == 1 && settings.Transparency == 80 && settings.Width == 270 && settings.Height == 80, "settings normalization");
var roundtrip = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(new UserSettings { Width=345, Height=222, Pinned=true, Transparency=60, IntervalMinutes=17 }));
Check(roundtrip is { Width:345, Height:222, Pinned:true, Transparency:60, IntervalMinutes:17 }, "settings persistence payload");
var executable = Environment.ProcessPath!;
try
{
    Environment.SetEnvironmentVariable("CODEX_PEEK_TEST_MODE", "success");
    var live = await new CodexClient().ReadAsync(executable);
    Check(live.Windows.Count == 1 && live.Windows[0].Remaining == 75, "JSON RPC handshake and notification handling");
    foreach (var mode in new[] { "error", "unauthenticated" })
    {
        Environment.SetEnvironmentVariable("CODEX_PEEK_TEST_MODE", mode);
        try { await new CodexClient().ReadAsync(executable); throw new Exception("Expected failure"); }
        catch (InvalidOperationException ex) { Check(!ex.Message.Contains("private diagnostic"), "safe " + mode + " error"); }
    }
    Environment.SetEnvironmentVariable("CODEX_PEEK_TEST_MODE", "wait");
    using var cancel = new CancellationTokenSource(500);
    try { await new CodexClient().ReadAsync(executable, cancel.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "cancellation terminates pending process"); }
}
finally { Environment.SetEnvironmentVariable("CODEX_PEEK_TEST_MODE", null); }
Console.WriteLine($"All {passed} checks passed.");
