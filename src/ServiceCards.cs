using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPeek;

public sealed partial class WidgetWindow
{
    private readonly GmailClient gmail = new();
    private readonly StackPanel services = new();
    private readonly ScrollViewer serviceScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly RowDefinition serviceRow = new() { Height = new GridLength(0) };
    private ConnectionsWindow? connectionsWindow;
    private MailSnapshot? mail;
    private ClaudeSnapshot? claude;
    private string? mailError;
    private string? claudeError;
    private bool servicesBusy;
    private readonly List<(LimitWindow Window, TextBlock Text)> serviceResetLabels = new();
    private DateTimeOffset nextServicesUpdate = DateTimeOffset.MinValue;
    private bool dashboardMode => ActualHeight >= 360 && settings.ShowServices;

    private void OpenConnections()
    {
        if (connectionsWindow is not null) { connectionsWindow.Activate(); return; }
        connectionsWindow = new ConnectionsWindow(gmail, settings, () =>
        {
            mail = null; claude = null; mailError = null; claudeError = null;
            SaveSettings(); RenderSnapshot(snapshot); _ = RefreshServicesAsync();
        }) { Owner = this };
        connectionsWindow.Closed += (_, _) => connectionsWindow = null;
        connectionsWindow.Show();
    }
    private async Task RefreshServicesAsync()
    {
        if (servicesBusy || closed || verifyFolder is not null) return;
        servicesBusy = true; RenderServiceCards();
        // Each provider handles its own failure, so Codex cannot prevent email updates.
        try
        {
            try { claude = ClaudeBridge.Read(); claudeError = null; }
            catch { claude = null; claudeError = "Claude 데이터를 읽지 못했습니다. 다시 연결해 주세요."; }
            RenderServiceCards();
            if (gmail.IsConnected)
            {
                try { mail = await gmail.ReadAsync(lifetime.Token); mailError = null; }
                catch (OperationCanceledException) when (closed) { }
                catch (Exception ex) { mailError = ex is InvalidOperationException ? ex.Message : "메일 조회 시간이 초과되었거나 연결이 끊겼습니다."; }
            }
            else { mail = null; mailError = null; }
        }
        finally
        {
            servicesBusy = false; nextServicesUpdate = DateTimeOffset.Now.AddMinutes(settings.IntervalMinutes);
            if (!closed) RenderServiceCards();
        }
    }
    private void RenderServiceCards()
    {
        serviceScroll.Visibility = dashboardMode ? Visibility.Visible : Visibility.Collapsed;
        serviceRow.Height = dashboardMode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        services.Children.Clear();
        serviceResetLabels.Clear();
        if (!dashboardMode) return;
        double scale = Math.Clamp(ActualWidth / 340, 1, 1.4);
        TextBlock Text(string value, double size = 12, bool strong = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap,
            FontSize = size * scale, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(strong ? Ink : Muted), Margin = new Thickness(0, 3, 0, 2) };
        StackPanel Card(string title)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(title, 14, true));
            services.Children.Add(new Border { Child = panel, Padding = new Thickness(9), Margin = new Thickness(0, 7, 2, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 130, 155, 143)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) });
            return panel;
        }
        var heading = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var connect = new Button { Content = "연결 관리", Padding = new Thickness(7, 4, 7, 4), HorizontalAlignment = HorizontalAlignment.Left };
        connect.Click += (_, _) => OpenConnections(); heading.Children.Add(connect); services.Children.Add(heading);
        var email = Card("이메일 · Gmail");
        if (mail is not null)
        {
            email.Children.Add(Text(mail.Account, 10));
            email.Children.Add(Text($"받은편지함 · 안 읽음 {mail.Unread:N0}개", 13, true));
            if (mail.Messages.Count == 0) email.Children.Add(Text("받은편지함이 비어 있습니다."));
            foreach (var message in mail.Messages)
            {
                var subject = Text(message.Subject, 12, true); subject.MaxHeight = 44 * scale; subject.TextTrimming = TextTrimming.CharacterEllipsis; subject.ToolTip = message.Subject;
                email.Children.Add(subject);
                var sender = Text(message.Sender, 10); sender.TextWrapping = TextWrapping.NoWrap; sender.TextTrimming = TextTrimming.CharacterEllipsis; sender.ToolTip = message.Sender;
                email.Children.Add(sender);
            }
            email.Children.Add(Text($"최근 조회 {mail.FetchedAt:MM/dd HH:mm}", 10));
        }
        else email.Children.Add(Text(mailError is not null ? "메일을 가져오지 못했습니다." : gmail.IsConnected ? "메일을 조회하고 있습니다." : "계정을 연결하면 안 읽은 메일 수와 최근 제목을 보여줍니다."));
        if (mailError is not null) email.Children.Add(Text((mail is not null ? "이전 조회 결과 · " : "") + mailError, 11));
        if (servicesBusy) email.Children.Add(Text("새로고침 중…", 10));
        var ai = Card("AI 사용량 · Claude");
        if (claude is not null && claude.Windows.Count > 0)
        {
            foreach (var window in claude.Windows)
            {
                ai.Children.Add(Text($"{window.Name} · {window.Remaining:0.#}% 남음", 12, true));
                ai.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = window.Remaining, Height = 6, Margin = new Thickness(0, 4, 0, 3), Foreground = new SolidColorBrush(Green) });
                var reset = Text(window.ResetText(DateTimeOffset.Now), 11);
                ai.Children.Add(reset); serviceResetLabels.Add((window, reset));
            }
            ai.Children.Add(Text($"마지막 전달 {claude.ReceivedAt:MM/dd HH:mm}\nClaude Code가 전달한 값", 10));
            if (DateTimeOffset.Now - claude.ReceivedAt > TimeSpan.FromMinutes(settings.IntervalMinutes)) ai.Children.Add(Text("이전 전달 값 · Claude Code 사용 후 갱신", 10));
        }
        else ai.Children.Add(Text(claude is not null ? "아직 한도 정보가 전달되지 않았습니다. Claude Code의 로그인과 버전을 확인하세요." : "연결 관리에서 Claude Code를 연결하세요. 5시간·주간 한도를 표시합니다."));
        if (claudeError is not null) ai.Children.Add(Text(claudeError, 11));
    }
}
