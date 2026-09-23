using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPeek;

public sealed class ConnectionsWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    public ConnectionsWindow(GmailClient gmail, UserSettings settings, Action changed)
    {
        Title = "이메일 · AI 연결"; Width = 470; Height = 640; MinWidth = 400; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        Background = new SolidColorBrush(Color.FromRgb(246, 249, 243)); FontSize = 13;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Text(string text, double size = 13) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 6, 0, 10) });
        Text("필요한 정보를 한 창에", 22);
        Text("높이 360부터 Codex·Gmail·Claude 카드를 좌우로 넘겨 보거나 세로로 모아 볼 수 있습니다. 연결은 이 PC에만 저장됩니다.");
        var show = new CheckBox { Content = "확대 화면에 이메일 · Claude 카드 표시", IsChecked = settings.ShowServices, Margin = new Thickness(0, 5, 0, 10) };
        show.Click += (_, _) => { settings.ShowServices = show.IsChecked == true; changed(); }; panel.Children.Add(show);
        Text("Gmail", 18);
        Text("받은편지함의 읽지 않은 메일 수와 최근 3개의 제목·보낸 사람을 표시합니다. 본문·첨부파일은 조회하지 않습니다.");
        Text("처음 한 번: Google Cloud에서 Gmail API를 켜고, OAuth 동의 화면과 테스트 사용자를 설정한 뒤 ‘데스크톱 앱’ OAuth 클라이언트 JSON을 받으세요.");
        var guide = new Button { Content = "Google 설정 안내 열기", Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
        guide.Click += (_, _) => OpenUrl("https://developers.google.com/workspace/gmail/api/quickstart/dotnet"); panel.Children.Add(guide);
        var connect = new Button { Content = gmail.IsConnected ? "Gmail 계정 다시 연결" : "JSON 선택 후 Google 로그인", Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 4) };
        var disconnect = new Button { Content = "Gmail 연결 해제", Padding = new Thickness(8), IsEnabled = gmail.IsConnected };
        var gmailState = new TextBlock { Text = gmail.IsConnected ? "연결됨" : "연결되지 않음", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 8) };
        panel.Children.Add(connect); panel.Children.Add(disconnect); panel.Children.Add(gmailState);
        connect.Click += async (_, _) =>
        {
            var file = new Microsoft.Win32.OpenFileDialog { Title = "Google 데스크톱 OAuth 클라이언트 JSON", Filter = "JSON (*.json)|*.json" };
            if (file.ShowDialog(this) != true) return;
            connect.IsEnabled = false; disconnect.IsEnabled = false; gmailState.Text = "브라우저에서 계정을 선택해 주세요. 최대 3분 동안 기다립니다.";
            try { await gmail.ConnectAsync(File.ReadAllText(file.FileName), lifetime.Token); gmailState.Text = "연결 완료 · 사용량 창에서 메일을 조회합니다."; changed(); }
            catch (OperationCanceledException) { gmailState.Text = "연결이 취소되었거나 대기 시간이 지났습니다."; }
            catch (Exception ex) { gmailState.Text = ex is InvalidOperationException ? ex.Message : "연결하지 못했습니다. JSON, 인터넷 연결, Google 앱 설정을 확인하세요."; }
            finally { connect.IsEnabled = true; disconnect.IsEnabled = gmail.IsConnected; }
        };
        disconnect.Click += async (_, _) => { await gmail.DisconnectAsync(); gmailState.Text = "이 PC의 연결 정보와 표시한 메일을 삭제했습니다."; disconnect.IsEnabled = false; changed(); };
        Text("Google 계정의 ‘서드 파티 연결’에서도 앱의 권한을 해제할 수 있습니다. 테스트 모드의 인증은 만료되어 재연결이 필요할 수 있습니다.", 11);
        Text("Claude Code", 18);
        Text("Claude Code의 상태 표시줄에서 5시간·주간 한도를 전달받습니다. 연결 후 Claude Code를 사용하면 수치가 나타납니다. 새로고침은 마지막 전달 값을 읽으며 서버에 직접 한도를 요청하지 않습니다.");
        var claude = new Button { Padding = new Thickness(8) };
        var claudeState = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        void UpdateClaude() => claude.Content = ClaudeBridge.IsConnected(Environment.ProcessPath!) ? "Claude 연결 해제" : "Claude Code 연결";
        UpdateClaude(); panel.Children.Add(claude); panel.Children.Add(claudeState);
        claude.Click += (_, _) =>
        {
            try
            {
                if (ClaudeBridge.IsConnected(Environment.ProcessPath!)) { ClaudeBridge.Disconnect(Environment.ProcessPath!); claudeState.Text = "연결을 해제했습니다."; }
                else { ClaudeBridge.Connect(Environment.ProcessPath!); claudeState.Text = "연결 완료. Claude Code에서 다음 응답을 받은 후 위젯을 새로고침하세요."; }
                UpdateClaude(); changed();
            }
            catch (Exception ex) { claudeState.Text = ex is InvalidOperationException ? ex.Message : "Claude 설정을 변경하지 못했습니다. 파일 권한과 JSON을 확인하세요."; }
        };
        Text("기존 상태 표시줄이 있으면 덮어쓰지 않습니다. 연결 전 설정 파일을 백업합니다. 계정 변경 후에는 재연결해 이전 수치를 지우세요.", 11);
        Closed += (_, _) => lifetime.Cancel();
    }
    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show("브라우저를 열지 못했습니다.", "Codex Peek"); }
    }
}
