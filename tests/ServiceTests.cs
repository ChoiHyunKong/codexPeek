using System.Net;
using System.Text;
using System.Text.Json;
using CodexPeek;

internal static class ServiceTests
{
    public static async Task VerifyWidgetBridge(string executable, Action<bool, string> check)
    {
        foreach (var mode in new[] { "utf8", "utf8-bom", "powershell" })
        {
            string destination = Path.Combine(Path.GetTempPath(), "peek-bridge-" + Guid.NewGuid().ToString("N") + ".json");
            var start = new System.Diagnostics.ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (mode == "powershell")
            {
                string encoded = ClaudeBridge.Command(executable).Split(' ').Last();
                string script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded)).Replace(" --claude-statusline", " --verify-claude-bridge '" + destination.Replace("'", "''") + "'");
                start.FileName = "powershell.exe";
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            }
            else { start.ArgumentList.Add("--verify-claude-bridge"); start.ArgumentList.Add(destination); }
            using var process = new System.Diagnostics.Process { StartInfo = start };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
                var input = Encoding.UTF8.GetBytes("""{"model":{"display_name":"한글 모델"},"rate_limits":{"five_hour":{"used_percentage":18,"resets_at":1900000000}}}""");
                if (mode != "utf8") await process.StandardInput.BaseStream.WriteAsync(new byte[] { 0xef, 0xbb, 0xbf }, timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token); process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token); await output; await error;
                var data = File.Exists(destination) ? JsonSerializer.Deserialize<ClaudeSnapshot>(File.ReadAllText(destination)) : null;
                if (process.ExitCode != 0 || data?.Windows.Single().Remaining != 82) Console.WriteLine("Bridge test diagnostics: " + await output + " " + await error);
                check(process.ExitCode == 0 && data?.Windows.Single().Remaining == 82, "Real WPF status-line input: " + mode);
            }
            finally { if (!process.HasExited) process.Kill(true); if (File.Exists(destination)) File.Delete(destination); }
        }
    }
    public static async Task Run(Action<bool, string> check)
    {
        using var data = JsonDocument.Parse("""{"session_id":"private-session","rate_limits":{"five_hour":{"used_percentage":20,"resets_at":1800000000},"seven_day":{"used_percentage":null}}}""");
        var snapshot = ClaudeBridge.Parse(data.RootElement, DateTimeOffset.Now);
        var command = ClaudeBridge.Command("C:/User's Folder/CodexPeek.exe");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ').Last()));
        check(script.Contains("& 'C:/User''s Folder/CodexPeek.exe'") && script.Contains("OpenStandardInput") && script.Contains("ReadToEnd() |"), "Claude command quotes executable paths and forwards UTF-8 stdin across Windows shells");
        var preferences = new UserSettings { ServiceOrder = new() { "claude", "invalid", "claude", "gmail" }, SelectedService = "invalid" };
        preferences.Normalize();
        check(preferences.ServiceOrder.SequenceEqual(new[] { "claude", "gmail", "codex" }) && preferences.SelectedService == "codex", "Card order removes unknown duplicates and restores missing services");
        var work = new DesktopRect(0, 0, 1920, 1040);
        check(DesktopPlacement.Fit(new(1600, 950, 270, 160), work) == new DesktopRect(1600, 880, 270, 160), "Widget stays above bottom taskbar");
        check(DesktopPlacement.Fit(new(-1900, 950, 400, 400), new(-1920, 0, 1920, 1040)) == new DesktopRect(-1900, 640, 400, 400), "Taskbar guard supports monitors with negative coordinates");
        check(DesktopPlacement.Fit(new(-20, -50, 270, 160), new(60, 40, 1860, 1000)) == new DesktopRect(60, 40, 270, 160), "Taskbar guard handles left and top docks");
        check(DesktopPlacement.Fit(new(0, 0, 3000, 2000), work) == new DesktopRect(0, 0, 1920, 1040), "Oversize widget fits available desktop");
        check(snapshot.Windows.Count == 1 && snapshot.Windows[0].Remaining == 80, "Claude partial quota does not fabricate missing weekly data");
        var temporary = Path.Combine(Path.GetTempPath(), "codexpeek-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ClaudeBridge.Capture(data.RootElement.GetRawText(), temporary);
            check(!File.ReadAllText(temporary).Contains("private-session"), "Claude bridge stores quota fields only");
            ClaudeBridge.Capture("{}", temporary);
            check(JsonSerializer.Deserialize<ClaudeSnapshot>(File.ReadAllText(temporary))!.Windows.Count == 0, "Claude missing quota clears older cached account data");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var secret = Encoding.UTF8.GetBytes("test-token-never-real");
        var encrypted = LocalSecret.Transform(secret, true);
        check(!encrypted.SequenceEqual(secret) && LocalSecret.Transform(encrypted, false).SequenceEqual(secret), "Windows user-bound secret encryption roundtrip");
        check(GmailClient.ParseClient("""{"installed":{"client_id":"example","client_secret":"secret"}}""").ClientId == "example", "Google desktop OAuth configuration");
        try { GmailClient.ParseClient("""{"web":{"client_id":"wrong-type"}}"""); throw new Exception("Expected desktop validation"); }
        catch (InvalidOperationException) { check(true, "Web client rejected for desktop login"); }
        check(GmailClient.ValidateCallback(new Uri("http://127.0.0.1/?state=expected&code=hello%2Bworld"), "expected") == "hello+world", "OAuth code decoded correctly");
        try { GmailClient.ValidateCallback(new Uri("http://127.0.0.1/?state=wrong&code=secret"), "expected"); throw new Exception("Expected state check"); }
        catch (InvalidOperationException) { check(true, "OAuth rejects unrelated browser callback"); }
        try { GmailClient.ValidateCallback(new Uri("http://127.0.0.1/?state=expected&error=access_denied"), "expected"); throw new Exception("Expected denial"); }
        catch (InvalidOperationException) { check(true, "OAuth denial does not connect"); }
        using var handler = new FakeGmail(); using var http = new HttpClient(handler);
        var mail = await GmailClient.FetchMailboxAsync(http, "fake-access-token", CancellationToken.None);
        check(mail.Unread == 7 && mail.Account == "test@example.com" && mail.Messages.Single().Subject == "(제목 없음)", "Gmail exact unread count and metadata-only message rendering");
        check(handler.Requests.All(p => !p.Contains("format=full") && !p.Contains("q=")) && handler.Requests.Any(p => p.Contains("format=metadata")), "Gmail uses metadata-compatible requests without body or search");
        handler.Fail = true;
        try { await GmailClient.FetchMailboxAsync(http, "fake", CancellationToken.None); throw new Exception("Expected forbidden response"); }
        catch (InvalidOperationException ex) { check(!ex.Message.Contains("private-response"), "Gmail failures do not expose server response"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await GmailClient.FetchMailboxAsync(http, "fake", cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "Gmail request cancellation"); }
    }
    private sealed class FakeGmail : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Unexpected Gmail request");
            string path = request.RequestUri!.PathAndQuery; Requests.Add(path);
            string body = path.EndsWith("profile") ? """{"emailAddress":"test@example.com"}"""
                : path.EndsWith("labels/INBOX") ? """{"messagesUnread":7}"""
                : path.Contains("/messages?") ? """{"messages":[{"id":"example"}],"resultSizeEstimate":999}"""
                : """{"payload":{"headers":[{"name":"From","value":"sender@example.com"}]}}""";
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(Fail ? "private-response" : body) });
        }
    }
}
