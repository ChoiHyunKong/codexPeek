using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly Grid serviceHost = new();
    private readonly StackPanel serviceToolbar = new();
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
    private DateTimeOffset nextBridgeCheck;
    private DateTime bridgeStamp;
    private DateTime bridgeErrorStamp;
    private bool claudeConnected;
    private TextBlock? dashboardCodexStatus;
    private bool dashboardMode => ActualHeight >= 360 && settings.ShowServices;

    private void InitializeServices()
    {
        serviceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        serviceHost.RowDefinitions.Add(new RowDefinition());
        serviceHost.Children.Add(serviceToolbar);
        serviceScroll.Content = services; Grid.SetRow(serviceScroll, 1); serviceHost.Children.Add(serviceScroll);
    }
    private static string ServiceName(string id) => id switch { "gmail" => "Gmail", "claude" => "Claude", _ => "Codex" };
    private void NavigateService(int direction)
    {
        settings.Normalize();
        int index = settings.ServiceOrder.IndexOf(settings.SelectedService);
        settings.SelectedService = settings.ServiceOrder[(index + direction + settings.ServiceOrder.Count) % settings.ServiceOrder.Count];
        SaveSettings(); RenderServiceCards(); serviceScroll.ScrollToTop();
    }
    private void MoveService(int direction)
    {
        settings.Normalize();
        int index = settings.ServiceOrder.IndexOf(settings.SelectedService);
        int next = index + direction;
        if (next < 0 || next >= settings.ServiceOrder.Count) return;
        (settings.ServiceOrder[index], settings.ServiceOrder[next]) = (settings.ServiceOrder[next], settings.ServiceOrder[index]);
        SaveSettings(); RenderServiceCards();
    }
    private void ReadClaude()
    {
        claudeConnected = verifyFolder is null && ClaudeBridge.IsConnected(Environment.ProcessPath!);
        try
        {
            claude = ClaudeBridge.Read();
            claudeError = File.Exists(ClaudeBridge.ErrorPath) ? "데이터 전달 실패 · 연결 관리에서 재연결해 주세요." : null;
            bridgeStamp = File.GetLastWriteTimeUtc(ClaudeBridge.CachePath);
            bridgeErrorStamp = File.GetLastWriteTimeUtc(ClaudeBridge.ErrorPath);
        }
        catch { claude = null; claudeError = "Claude 데이터를 읽지 못했습니다. 다시 연결해 주세요."; }
    }
    private void CheckClaudeBridge()
    {
        if (verifyFolder is not null || DateTimeOffset.Now < nextBridgeCheck) return;
        nextBridgeCheck = DateTimeOffset.Now.AddSeconds(2);
        try
        {
            if (File.GetLastWriteTimeUtc(ClaudeBridge.CachePath) == bridgeStamp && File.GetLastWriteTimeUtc(ClaudeBridge.ErrorPath) == bridgeErrorStamp) return;
            ReadClaude(); RenderServiceCards();
        }
        catch { /* A transient local file error is shown by the next explicit refresh. */ }
    }

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
            ReadClaude();
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
        serviceHost.Visibility = dashboardMode ? Visibility.Visible : Visibility.Collapsed;
        serviceRow.Height = dashboardMode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        services.Children.Clear();
        serviceToolbar.Children.Clear(); dashboardCodexStatus = null;
        serviceResetLabels.Clear();
        if (!dashboardMode) return;
        double scale = Math.Clamp(ActualWidth / 340, 1, 1.4);
        TextBlock Text(string value, double size = 12, bool strong = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap,
            FontSize = size * scale, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(strong ? Ink : Muted), Margin = new Thickness(0, 3, 0, 2) };
        var cards = new Dictionary<string, Border>();
        StackPanel Card(string id, string title)
        {
            var panel = new StackPanel();
            var cardTitle = Text(title, 14, true);
            cardTitle.Cursor = System.Windows.Input.Cursors.Hand;
            cardTitle.ToolTip = "이 카드를 선택하여 순서 변경";
            cardTitle.MouseLeftButtonDown += (_, e) => { e.Handled = true; settings.SelectedService = id; SaveSettings(); RenderServiceCards(); };
            panel.Children.Add(cardTitle);
            cards[id] = new Border { Tag = id, Child = panel, Padding = new Thickness(9), Margin = new Thickness(0, 7, 2, 0),
                BorderBrush = new SolidColorBrush(settings.SelectedService == id ? Green : Color.FromArgb(100, 130, 155, 143)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
            return panel;
        }
        Button ActionButton(string label, string name, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(6, 4, 6, 4), MinHeight = 27, ToolTip = name,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Muted),
                Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11 };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => action(); return button;
        }
        var navigation = new Grid();
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
        navigation.ColumnDefinitions.Add(new ColumnDefinition());
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
        navigation.Children.Add(ActionButton("‹", "이전 서비스", () => NavigateService(-1)));
        int current = settings.ServiceOrder.IndexOf(settings.SelectedService);
        var title = new Label { Content = $"{ServiceName(settings.SelectedService)}  {current + 1}/{settings.ServiceOrder.Count}",
            FontSize = 12 * scale, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Ink),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
        Grid.SetColumn(title, 1); navigation.Children.Add(title);
        var nextButton = ActionButton("›", "다음 서비스", () => NavigateService(1)); Grid.SetColumn(nextButton, 2); navigation.Children.Add(nextButton);
        serviceToolbar.Children.Add(navigation);
        var actions = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var width in new[] { 1.2, 1.0, 1.0 }) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        var actionButtons = new[] {
            ActionButton(settings.ServiceCarousel ? "모두 보기" : "한 장씩", "가로 넘김 / 세로 모두 보기", () => { settings.ServiceCarousel = !settings.ServiceCarousel; SaveSettings(); RenderServiceCards(); serviceScroll.ScrollToTop(); }),
            ActionButton("앞으로", "선택한 카드를 앞 순서로 이동", () => MoveService(-1)),
            ActionButton("뒤로", "선택한 카드를 뒤 순서로 이동", () => MoveService(1)) };
        actionButtons[1].IsEnabled = current > 0; actionButtons[2].IsEnabled = current < settings.ServiceOrder.Count - 1;
        for (int i = 0; i < actionButtons.Length; i++) { Grid.SetColumn(actionButtons[i], i); actions.Children.Add(actionButtons[i]); }
        serviceToolbar.Children.Add(actions);
        void Quota(StackPanel panel, LimitWindow window)
        {
            panel.Children.Add(Text($"{window.Name} · {window.Remaining:0.#}% 남음", 13, true));
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = window.Remaining, Height = 6, Margin = new Thickness(0, 4, 0, 3), Foreground = new SolidColorBrush(Green) });
            var reset = Text(window.ResetText(DateTimeOffset.Now), 11);
            panel.Children.Add(reset); serviceResetLabels.Add((window, reset));
            panel.Children.Add(Text(AbsoluteReset(window), 10));
        }
        var codexCard = Card("codex", "AI 사용량 · Codex");
        if (snapshot is null) codexCard.Children.Add(Text(state.Text));
        else if (snapshot.Windows.Count == 0) codexCard.Children.Add(Text("계정에서 한도 정보를 제공하지 않았습니다."));
        else foreach (var window in snapshot.Windows) Quota(codexCard, window);
        dashboardCodexStatus = Text(footer.Text, 10); codexCard.Children.Add(dashboardCodexStatus);
        var email = Card("gmail", "이메일 · Gmail");
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
        var ai = Card("claude", "AI 사용량 · Claude");
        if (claude is not null && claude.Windows.Count > 0)
        {
            foreach (var window in claude.Windows)
                Quota(ai, window);
            ai.Children.Add(Text($"마지막 전달 {claude.ReceivedAt:MM/dd HH:mm}\nClaude Code가 전달한 값", 10));
            if (DateTimeOffset.Now - claude.ReceivedAt > TimeSpan.FromMinutes(settings.IntervalMinutes)) ai.Children.Add(Text("이전 전달 값 · Claude Code 사용 후 갱신", 10));
        }
        else
        {
            ai.Children.Add(Text(claudeConnected ? "연결됨 · 한도 데이터 대기" : "Claude Code 연결 필요", 13, true));
            ai.Children.Add(Text(claudeConnected
                ? claude is null ? "Claude Code를 다시 열거나 다음 응답을 기다리세요. 데이터가 도착하면 자동으로 표시됩니다."
                    : "데이터는 도착했지만 한도 항목이 없습니다. 지원 구독 계정으로 Claude Code를 사용한 뒤 확인하세요."
                : "환경설정에서 Claude Code를 연결하세요. 5시간·주간 한도를 표시합니다."));
        }
        if (claudeError is not null) ai.Children.Add(Text(claudeError, 11));
        foreach (string id in settings.ServiceOrder)
            if (!settings.ServiceCarousel || id == settings.SelectedService) services.Children.Add(cards[id]);
    }
}
