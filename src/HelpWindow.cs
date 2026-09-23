using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexPeek;

public sealed class HelpWindow : Window
{
    private readonly TabControl topics;
    public HelpWindow()
    {
        Title = "Codex Peek 도움말";
        Width = 580; Height = 650; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 249, 243));
        Foreground = new SolidColorBrush(Color.FromRgb(29, 43, 39));
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 13;
        var root = new Grid { Margin = new Thickness(22) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        intro.Children.Add(WidgetWindow.Label("Codex Peek 사용 안내", 23, weight: FontWeights.SemiBold));
        intro.Children.Add(new TextBlock { Text = "사용 한도를 읽는 방법부터 다른 PC에서 사용하는 방법까지", Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(103, 120, 112)) });
        root.Children.Add(intro);
        topics = new TabControl { Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(207, 221, 211)), Padding = new Thickness(0) };
        Grid.SetRow(topics, 1); root.Children.Add(topics);
        AddTopic("사용 방법",
            ("무엇을 보여주나요?", "현재 Codex 계정의 한도 종류, 남은 비율, 리셋까지 남은 시간을 보여줍니다. 그래프는 남은 비율을 뜻합니다. 계정에서 주간 한도만 제공하면 주간 한도만 표시합니다."),
            ("작은 창과 확대 화면", "작은 창에서도 한도명·남은 비율·리셋 시간·그래프를 함께 표시합니다. 창을 가로 340, 세로 260 이상으로 키우면 사용한 비율, 정확한 리셋 날짜, 조회 상태와 갱신 일정이 추가됩니다. 확대할수록 글자도 커집니다."),
            ("이동 · 크기 · 최상단 고정", "상단의 빈 곳을 드래그하면 이동하고, 테두리나 모서리를 드래그하면 크기가 바뀝니다. 핀 버튼으로 다른 일반 창 위에 고정하거나 해제합니다. 최소 높이는 표시 내용에 맞춰 정해집니다."),
            ("설정과 단축키", "톱니바퀴에서 갱신 간격, 배경 투명도, Windows 시작 시 실행을 설정합니다. F5는 즉시 새로고침, F1은 도움말입니다. 이 도움말은 Esc로 닫을 수 있습니다."),
            ("숨기기와 종료", "상단의 ― 버튼은 트레이로 숨깁니다. 트레이 아이콘을 더블클릭하면 다시 표시됩니다. 완전히 종료하려면 트레이 메뉴의 종료 또는 위젯에서 Alt+F4를 사용합니다."));
        AddTopic("계정·다른 PC",
            ("어느 계정의 사용량인가요?", "이 PC에서 위젯이 실행한 Codex CLI의 로그인 계정으로 서버 한도를 조회합니다. 브라우저에서 ChatGPT 계정만 바꿔도 위젯의 조회 계정이 자동으로 바뀌는 것은 아닙니다."),
            ("다른 PC에서 같은 계정을 쓰면?", "같은 계정과 같은 워크스페이스로 로그인하면 그 계정의 한도를 조회합니다. 다른 PC에서 사용한 양도 서버 집계 후 새로고침이나 다음 자동 갱신 때 반영됩니다. PC별 사용량을 따로 합산하는 앱은 아닙니다."),
            ("다른 PC에서 시작하기", "① app 폴더 전체를 복사합니다.\n② .NET 10 Desktop Runtime과 Codex를 설치합니다.\n③ Codex에 원하는 ChatGPT 계정으로 로그인합니다.\n④ CodexPeek.exe를 실행합니다.\n위젯 자체에는 별도 로그인 화면이 없습니다."),
            ("계정을 바꿨을 때", "Codex CLI의 로그인 계정을 변경한 뒤 위젯에서 새로고침하세요. 조회에 성공하면 새 계정의 한도로 교체됩니다. 현재 버전에는 조회 계정 표시·계정 선택 기능이 없으며, 변경 후 조회가 실패하면 이전 값이 남을 수 있습니다."),
            ("현재 지원 범위", "한 번에 하나의 ChatGPT 로그인 계정을 조회합니다. API 키 기반 토큰·비용 추적이나 여러 계정 동시 조회는 지원하지 않습니다. 로그인 정보는 Codex가 관리하며 위젯이 별도로 저장하지 않습니다."));
        AddTopic("갱신·문제 해결",
            ("언제 갱신되나요?", "실행 시 바로 조회하고 기본 30분마다 다시 조회합니다. 설정에서 1~1,440분 사이의 정수로 바꿀 수 있습니다. 새로고침 또는 F5를 누르면 바로 요청하며, 응답이 오면 표시를 바꿉니다. 성공한 시각을 기준으로 다음 갱신을 계산합니다."),
            ("카운트다운은 줄어드는데 비율은 그대로예요", "리셋까지 남은 시간은 매초 계산하지만 서버 사용량 조회는 설정된 간격으로 수행합니다. 서버 집계가 늦어질 수도 있습니다. 리셋 시각이 지나도 서버 응답 없이 잔여량을 임의로 100%로 바꾸지 않습니다."),
            ("연결 실패 또는 로그인 필요", "Codex의 ChatGPT 로그인 상태와 인터넷 연결을 확인한 후 새로고침하세요. Codex를 찾지 못하면 설정에서 codex.exe 경로를 선택하세요. 응답 제한 시간은 30초입니다. 실패하면 이전 값과 실패 안내를 유지하고 설정 간격 후 재시도합니다."),
            ("설정과 위치는 어디에 저장되나요?", "창 크기·위치·핀 상태·투명도·갱신 간격은 이 PC에 저장되어 다음 실행에 복원됩니다. 다른 PC로 자동 동기화되지는 않습니다. 설정 파일은 %LOCALAPPDATA%\\CodexPeek\\settings.json에 있습니다."),
            ("더 알아보기", "이 안내는 인터넷 없이 읽을 수 있습니다. 아래 전체 설명서 버튼에서 기능 설명과 개발 히스토리를 확인할 수 있습니다. Codex Peek는 개인 프로젝트이며 OpenAI의 공식 제품은 아닙니다."));
        var actions = new Grid { Margin = new Thickness(0, 15, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var readme = new Button { Content = "GitHub 전체 설명서", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        readme.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/ChoiHyunKong/codexPeek#readme") { UseShellExecute = true }); }
            catch { MessageBox.Show(this, "브라우저를 열지 못했습니다. github.com/ChoiHyunKong/codexPeek에서 설명서를 볼 수 있습니다.", "Codex Peek 도움말"); }
        };
        var close = new Button { Content = "닫기", Padding = new Thickness(18, 7, 18, 7), IsCancel = true };
        close.Click += (_, _) => Close();
        actions.Children.Add(readme); Grid.SetColumn(close, 1); actions.Children.Add(close); Grid.SetRow(actions, 2); root.Children.Add(actions);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    private void AddTopic(string title, params (string Title, string Body)[] sections)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        foreach (var section in sections)
        {
            panel.Children.Add(new TextBlock { Text = section.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(37, 121, 94)), Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = section.Body, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 0, 18) });
        }
        topics.Items.Add(new TabItem { Header = title, Padding = new Thickness(12, 8, 12, 8), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
    }
    internal async Task VerifyPagesAsync(string folder)
    {
        for (int index = 0; index < topics.Items.Count; index++)
        {
            topics.SelectedIndex = index;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var view = (ScrollViewer)((TabItem)topics.Items[index]).Content;
            if (view.ViewportWidth <= 0 || view.ScrollableWidth > 0) throw new Exception("Help page has horizontal overflow");
            async Task Capture(string suffix)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var content = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                    drawing.DrawRectangle(Background, null, bounds);
                    drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, bounds);
                }
                bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, $"help-{index + 1}-{suffix}.png")); encoder.Save(stream);
            }
            await Capture("top"); view.ScrollToBottom(); await Capture("bottom");
        }
    }
}
