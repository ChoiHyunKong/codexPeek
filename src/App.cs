using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexPeek;
public sealed class App : Application
{
    private Mutex? mutex;
    [STAThread] public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            try
            {
                var result = new CodexClient().ReadAsync(null).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { ok = true, windows = result.Windows, fetchedAt = result.FetchedAt }));
            }
            catch (Exception ex) { File.WriteAllText(args[1], JsonSerializer.Serialize(new { ok = false, error = ex.Message })); Environment.ExitCode = 1; }
            return;
        }
        var app = new App();
        bool verify = args.Length > 0 && args[0] == "--verify-ui";
        if (!verify)
        {
            app.mutex = new Mutex(true, "Local\\CodexPeek.DesktopWidget", out var first);
            if (!first) { MessageBox.Show("Codex Peek가 이미 실행 중입니다. 시스템 트레이에서 열어 주세요.", "Codex Peek"); app.mutex.Dispose(); return; }
        }
        app.Exit += (_, _) => app.mutex?.Dispose();
        app.Run(new WidgetWindow(verify ? args[1] : null));
    }
}

public sealed class WidgetWindow : Window
{
    private static readonly Color Ink = Color.FromRgb(29, 43, 39);
    private static readonly Color Green = Color.FromRgb(37, 121, 94);
    private static readonly Color Muted = Color.FromRgb(103, 120, 112);
    private readonly UserSettings settings;
    private readonly UpdateSchedule schedule = new();
    private readonly CodexClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Border surface;
    private readonly StackPanel rows = new();
    private readonly TextBlock footer;
    private readonly RowDefinition footerRow = new() { Height = new GridLength(20) };
    private readonly TextBlock state;
    private readonly Button refreshButton;
    private readonly Button pinButton;
    private readonly List<(LimitWindow Window, TextBlock Text)> resetLabels = new();
    private readonly Forms.NotifyIcon? tray;
    private UsageSnapshot? snapshot;
    private SettingsWindow? settingsWindow;
    private bool busy;
    private bool closed;
    private bool hadError;
    private enum WidgetLayout { Compact, Standard, Expanded }
    private WidgetLayout layoutMode;
    private bool compactMode => layoutMode == WidgetLayout.Compact;
    private bool expandedMode => layoutMode == WidgetLayout.Expanded;
    private WidgetLayout SelectLayout() => ActualWidth >= 340 && ActualHeight >= 260
        ? WidgetLayout.Expanded : ActualHeight < 150 ? WidgetLayout.Compact : WidgetLayout.Standard;
    private readonly string? verifyFolder;

    public WidgetWindow(string? verifyFolder = null)
    {
        this.verifyFolder = verifyFolder;
        settings = verifyFolder is null ? SettingsStore.Load() : new UserSettings();
        Title = "Codex Peek";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12; Foreground = new SolidColorBrush(Ink);
        Width = settings.Width; Height = settings.Height; MinWidth = 220; MinHeight = 130;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Topmost = settings.Pinned;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = settings.Left ?? Math.Max(0, SystemParameters.WorkArea.Right - Width - 24);
        Top = settings.Top ?? 48;
        schedule.Configure(settings.IntervalMinutes, DateTimeOffset.Now);

        var root = new Grid(); Content = root;
        surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(100, 113, 143, 128)), Padding = new Thickness(13, 9, 13, 8) };
        root.Children.Add(surface); ApplyTransparency(settings.Transparency);
        var layout = new Grid(); surface.Child = layout;
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(footerRow);

        var header = new Grid { Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Green), Margin = new Thickness(0, 0, 7, 0) });
        brand.Children.Add(Label("Codex Peek", 13, Ink, FontWeights.SemiBold));
        header.Children.Add(brand);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        pinButton = IconButton("\uE718", "최상단 고정", (_, _) => TogglePin());
        refreshButton = IconButton("\uE72C", "지금 새로고침", async (_, _) => await RefreshAsync());
        controls.Children.Add(pinButton); controls.Children.Add(refreshButton);
        controls.Children.Add(IconButton("\uE713", "설정", (_, _) => OpenSettings()));
        controls.Children.Add(IconButton("\uE921", "트레이로 숨기기", (_, _) => Hide()));
        Grid.SetColumn(controls, 1); header.Children.Add(controls); layout.Children.Add(header);
        var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
        state = Label("계정 한도를 확인하고 있습니다…", 11, Muted);
        state.TextWrapping = TextWrapping.Wrap; rows.Children.Add(state);
        footer = Label("연결 준비 중", 9, Muted);
        footer.VerticalAlignment = VerticalAlignment.Bottom; footer.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        AddResizeHandles(root);
        UpdatePin();

        if (verifyFolder is null)
        {
            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Codex Peek · 사용량 위젯", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("위젯 표시", null, (_, _) => Dispatcher.Invoke(ShowWidget));
            menu.Items.Add("지금 새로고침", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync()));
            menu.Items.Add("최상단 고정 / 해제", null, (_, _) => Dispatcher.Invoke(TogglePin));
            menu.Items.Add("설정", null, (_, _) => Dispatcher.Invoke(OpenSettings));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(Close));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
            SystemEvents.PowerModeChanged += PowerChanged;
        }
        timer.Tick += async (_, _) => { UpdateTimeLabels(); if (schedule.IsDue(DateTimeOffset.Now)) await RefreshAsync(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        LocationChanged += (_, _) => QueueSave(); SizeChanged += (_, _) => { QueueSave(); if (snapshot is not null && layoutMode != SelectLayout()) RenderSnapshot(snapshot); };
        Loaded += async (_, _) =>
        {
            EnsureVisible();
            if (verifyFolder is not null)
            {
                try { await VerifyUiAsync(verifyFolder); }
                catch (Exception ex)
                {
                    File.WriteAllText(System.IO.Path.Combine(verifyFolder, "ui-check.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString() }));
                    Environment.ExitCode = 1; Close();
                }
                return;
            }
            timer.Start(); await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            closed = true; timer.Stop(); saveTimer.Stop(); lifetime.Cancel();
            SystemEvents.PowerModeChanged -= PowerChanged;
            settingsWindow?.Close(); tray?.Dispose(); SaveSettings();
        };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync(); } };
    }
    internal static TextBlock Label(string text, double size = 12, Color? color = null, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = new SolidColorBrush(color ?? Ink), FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    private static Button IconButton(string symbol, string tooltip, RoutedEventHandler click)
    {
        var b = new Button { Content = symbol, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Width = 25, Height = 25, ToolTip = tooltip, Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Muted), Padding = new Thickness(0), Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        b.Click += click; return b;
    }
    private void TogglePin() { settings.Pinned = !settings.Pinned; Topmost = settings.Pinned; UpdatePin(); SaveSettings(); }
    private void UpdatePin()
    {
        pinButton.Foreground = new SolidColorBrush(settings.Pinned ? Green : Muted);
        pinButton.Background = settings.Pinned ? new SolidColorBrush(Color.FromArgb(45, 37, 121, 94)) : Brushes.Transparent;
        pinButton.Content = settings.Pinned ? "\uE77A" : "\uE718";
        pinButton.ToolTip = settings.Pinned ? "최상단 고정 해제" : "최상단 고정";
        System.Windows.Automation.AutomationProperties.SetName(pinButton, (string)pinButton.ToolTip);
    }
    internal void ApplyTransparency(int value) => surface.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (1 - value / 100.0)), 246, 249, 243));
    private void ShowWidget() { Show(); WindowState = WindowState.Normal; EnsureVisible(); Activate(); }
    private void EnsureVisible()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            var bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            bool usable = Forms.Screen.AllScreens.Any(s => { var r = System.Drawing.Rectangle.Intersect(s.WorkingArea, bounds); return r.Width >= 100 && r.Height >= 50; });
            if (!usable) { Left = SystemParameters.WorkArea.Left + 24; Top = SystemParameters.WorkArea.Top + 24; }
        }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(async () => { if (schedule.IsDue(DateTimeOffset.Now)) await RefreshAsync(); });
    }
    private async Task RefreshAsync()
    {
        if (busy || closed) return;
        busy = true; refreshButton.IsEnabled = false; footer.Text = "최신 사용량을 조회하고 있습니다…"; UpdateFooterVisibility();
        try
        {
            snapshot = await client.ReadAsync(settings.CodexPath, lifetime.Token);
            if (closed) return;
            schedule.Succeeded(snapshot.FetchedAt); hadError = false;
            RenderSnapshot(snapshot);
            if (tray is not null) tray.Text = snapshot.Windows.Count > 0 ? $"Codex Peek · {snapshot.Windows[0].Remaining:0.#}% 남음" : "Codex Peek · 한도 정보 없음";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (closed) return;
            hadError = true; schedule.Failed(DateTimeOffset.Now);
            var message = ex is InvalidOperationException ? ex.Message : "사용량을 가져오지 못했습니다. Codex 설치와 네트워크를 확인하세요.";
            footer.ToolTip = message;
            if (snapshot is null) { rows.Children.Clear(); state.Text = message; rows.Children.Add(state); }
        }
        finally { busy = false; refreshButton.IsEnabled = true; if (!closed) UpdateTimeLabels(); }
    }
    private static string AbsoluteReset(LimitWindow window)
    {
        if (window.ResetAt is not long epoch) return "정확한 리셋 시각 정보 없음";
        try { return "리셋 " + DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("yyyy.MM.dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ko-KR")); }
        catch (ArgumentOutOfRangeException) { return "정확한 리셋 시각 정보 없음"; }
    }
    private void RenderSnapshot(UsageSnapshot data)
    {
        rows.Children.Clear(); resetLabels.Clear(); layoutMode = SelectLayout();
        if (data.Windows.Count == 0) { state.Text = "계정에서 표시 가능한 한도 정보를 제공하지 않았습니다."; rows.Children.Add(state); }
        foreach (var w in data.Windows)
        {
            var box = new Grid { Margin = new Thickness(0, 1, 2, expandedMode ? 3 : 1) };
            box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            box.RowDefinitions.Add(new RowDefinition { Height = new GridLength(expandedMode ? 10 : 7) });
            box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // The heading, countdown, remaining percentage and bar are essential at every size.
            var title = new Grid { Name = "QuotaHeading" };
            title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Label(w.Name, compactMode ? 10 : expandedMode ? 12 : 11, Muted);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = w.Name;
            var number = Label($"{w.Remaining:0.#}% 남음", compactMode ? 11 : expandedMode ? 15 : 13, w.Remaining <= 10 ? Color.FromRgb(185, 78, 55) : Green, FontWeights.SemiBold);
            number.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(number, 1); title.Children.Add(name); title.Children.Add(number); box.Children.Add(title);
            var track = new Grid { Name = "QuotaBar", Height = 6, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Background = Brushes.Transparent };
            System.Windows.Automation.AutomationProperties.SetName(track, $"{w.Name}, {w.Remaining:0.#}% 남음");
            track.ToolTip = "";
            track.ToolTipOpening += (_, _) => track.ToolTip = $"{w.Name} · {w.Remaining:0.#}% 남음\n{w.ResetText(DateTimeOffset.Now)}\n{AbsoluteReset(w)}\n최근 갱신 {data.FetchedAt:MM/dd HH:mm}";
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(50, 82, 127, 106)), CornerRadius = new CornerRadius(3) });
            var fill = new Border { Background = new SolidColorBrush(w.Remaining <= 10 ? Color.FromRgb(185, 78, 55) : Green), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * w.Remaining / 100;
            track.Children.Add(fill); Grid.SetRow(track, 1); box.Children.Add(track);
            var reset = Label(w.ResetText(DateTimeOffset.Now), compactMode ? 9 : 10, Muted);
            reset.Name = "QuotaReset"; reset.TextTrimming = TextTrimming.CharacterEllipsis; reset.ToolTip = AbsoluteReset(w);
            Grid.SetRow(reset, 2); box.Children.Add(reset); resetLabels.Add((w, reset));
            var detail = new StackPanel { Name = "QuotaDetails", Visibility = expandedMode ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 3, 0, 0) };
            detail.Children.Add(Label($"사용한 비율 {100 - w.Remaining:0.#}%", 10, Muted));
            detail.Children.Add(Label(AbsoluteReset(w), 10, Muted));
            Grid.SetRow(detail, 3); box.Children.Add(detail); rows.Children.Add(box);
        }
        if (!hadError) footer.ToolTip = null;
        UpdateTimeLabels();
    }
    private void UpdateFooterVisibility()
    {
        bool show = !compactMode || hadError || busy;
        footer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        footer.FontSize = expandedMode ? 10 : 9;
        footerRow.Height = new GridLength(show ? expandedMode ? 50 : 20 : 0);
    }
    private void UpdateTimeLabels()
    {
        var now = DateTimeOffset.Now;
        UpdateFooterVisibility();
        foreach (var (w, text) in resetLabels) text.Text = w.ResetText(now);
        if (busy) { footer.Text = "최신 사용량을 조회하고 있습니다…"; return; }
        if (hadError)
        {
            footer.Text = schedule.LastSuccess is { } last
                ? expandedMode ? $"갱신 실패 · 이전 조회 결과 표시 중\n최근 성공 {last:MM/dd HH:mm} · 재시도 {schedule.NextAttempt:MM/dd HH:mm}\n자동 갱신 {settings.IntervalMinutes}분 간격 · ↻ 즉시 재시도"
                    : $"갱신 실패 · 최근 {last:MM/dd HH:mm} · ↻ 재시도"
                : "연결 실패 · ↻ 재시도 / ⚙ 설정";
            footer.Foreground = new SolidColorBrush(Color.FromRgb(172, 87, 54));
        }
        else
        {
            footer.Text = schedule.LastSuccess is { } last
                ? expandedMode ? $"조회 정상 · 자동 갱신 {settings.IntervalMinutes}분 간격\n최근 갱신 {last:MM/dd HH:mm:ss}\n다음 갱신 {schedule.NextAttempt:MM/dd HH:mm:ss}"
                    : $"최근 {last:HH:mm}  ·  다음 {schedule.NextAttempt:HH:mm}"
                : "연결 준비 중";
            footer.Foreground = new SolidColorBrush(Muted);
            footer.ToolTip = schedule.LastSuccess is { } time ? $"마지막 조회 성공: {time:yyyy-MM-dd HH:mm:ss}\n다음 조회: {schedule.NextAttempt:yyyy-MM-dd HH:mm:ss}" : null;
        }
    }
    private void QueueSave() { if (!IsLoaded || verifyFolder is not null) return; saveTimer.Stop(); saveTimer.Start(); }
    private void SaveSettings()
    {
        if (verifyFolder is not null) return;
        settings.Width = Width; settings.Height = Height; settings.Left = Left; settings.Top = Top;
        try { SettingsStore.Save(settings); }
        catch { footer.Text = "설정을 저장하지 못했습니다."; }
    }
    private void OpenSettings()
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        ShowWidget();
        settingsWindow = new SettingsWindow(settings, ApplyTransparency, SaveOptions) { Owner = this };
        settingsWindow.Closed += (_, _) => { settingsWindow = null; ApplyTransparency(settings.Transparency); };
        settingsWindow.Show();
    }
    private void SaveOptions(int minutes, int transparency, bool startup, string? path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (startup) key.SetValue("CodexPeek", $"\"{Environment.ProcessPath}\""); else key.DeleteValue("CodexPeek", false);
        settings.IntervalMinutes = minutes; settings.Transparency = transparency; settings.StartWithWindows = startup; settings.CodexPath = path;
        schedule.Configure(minutes, DateTimeOffset.Now); ApplyTransparency(transparency); SaveSettings(); UpdateTimeLabels();
        if (schedule.IsDue(DateTimeOffset.Now)) _ = RefreshAsync();
    }
    private void AddResizeHandles(Grid grid)
    {
        void Grip(HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height, Cursor cursor, int hit)
        {
            var grip = new Border { Background = Brushes.Transparent, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Cursor = cursor };
            if (width > 0) grip.Width = width; if (height > 0) grip.Height = height;
            grip.MouseLeftButtonDown += (_, e) => { e.Handled = true; ReleaseCapture(); SendMessage(new WindowInteropHelper(this).Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); };
            grid.Children.Add(grip);
        }
        Grip(HorizontalAlignment.Left, VerticalAlignment.Stretch, 5, 0, Cursors.SizeWE, 10);
        Grip(HorizontalAlignment.Right, VerticalAlignment.Stretch, 5, 0, Cursors.SizeWE, 11);
        Grip(HorizontalAlignment.Stretch, VerticalAlignment.Top, 0, 5, Cursors.SizeNS, 12);
        Grip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, 0, 5, Cursors.SizeNS, 15);
        Grip(HorizontalAlignment.Left, VerticalAlignment.Top, 9, 9, Cursors.SizeNWSE, 13);
        Grip(HorizontalAlignment.Right, VerticalAlignment.Top, 9, 9, Cursors.SizeNESW, 14);
        Grip(HorizontalAlignment.Left, VerticalAlignment.Bottom, 9, 9, Cursors.SizeNESW, 16);
        Grip(HorizontalAlignment.Right, VerticalAlignment.Bottom, 12, 12, Cursors.SizeNWSE, 17);
    }
    private async Task VerifyUiAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.Now;
        snapshot = new UsageSnapshot(new List<LimitWindow> { new("일간 한도", 28, now.AddHours(2).AddMinutes(18).ToUnixTimeSeconds()), new("주간 한도", 31, now.AddDays(5).AddHours(2).AddSeconds(30).ToUnixTimeSeconds()) }, "demo", now);
        schedule.Succeeded(now); RenderSnapshot(snapshot);
        int rendered = 0;
        async Task Capture(string name, double width, double height)
        {
            Width = width; Height = height;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width * 2, (int)height * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render((Visual)Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(stream); rendered++;
        }
        void CheckQuotaLayout(WidgetLayout expected)
        {
            if (layoutMode != expected) throw new Exception("Wrong responsive layout");
            foreach (var box in rows.Children.OfType<Grid>())
            {
                var bar = box.Children.OfType<Grid>().Single(g => g.Name == "QuotaBar");
                var heading = box.Children.OfType<Grid>().Single(g => g.Name == "QuotaHeading");
                var reset = box.Children.OfType<TextBlock>().Single(t => t.Name == "QuotaReset");
                var detail = box.Children.OfType<StackPanel>().Single(p => p.Name == "QuotaDetails");
                if (!bar.IsVisible || bar.ActualHeight < 4 || bar.ActualWidth <= 0) throw new Exception("Quota bar disappeared");
                if (!heading.IsVisible || !reset.IsVisible) throw new Exception("Essential quota labels disappeared");
                if (detail.IsVisible != (expected == WidgetLayout.Expanded)) throw new Exception("Incorrect expanded details");
                var viewport = (ScrollViewer)rows.Parent;
                if (viewport.ScrollableHeight > 0.5) throw new Exception("Two quota rows should fit without scrolling");
                var bounds = box.TranslatePoint(new Point(0, 0), rows);
                if (bounds.Y + box.ActualHeight > viewport.ViewportHeight + 0.5) throw new Exception("Quota content clipped");
            }
            if (footer.IsVisible != (expected != WidgetLayout.Compact)) throw new Exception("Incorrect footer visibility");
        }
        await Capture("default-demo", 270, 160); CheckQuotaLayout(WidgetLayout.Standard);
        await Capture("minimum-demo", 220, 130); CheckQuotaLayout(WidgetLayout.Compact);
        await Capture("threshold-compact-demo", 270, 149); CheckQuotaLayout(WidgetLayout.Compact);
        await Capture("threshold-full-demo", 270, 150); CheckQuotaLayout(WidgetLayout.Standard);
        await Capture("expanded-demo", 380, 300); CheckQuotaLayout(WidgetLayout.Expanded);
        await Capture("expanded-boundary-demo", 340, 260); CheckQuotaLayout(WidgetLayout.Expanded);
        await Capture("narrow-tall-demo", 220, 300); CheckQuotaLayout(WidgetLayout.Standard);
        await Capture("wide-short-demo", 400, 130); CheckQuotaLayout(WidgetLayout.Compact);
        await Capture("restored-demo", 270, 160); CheckQuotaLayout(WidgetLayout.Standard);
        hadError = true; schedule.Failed(now.AddMinutes(1)); UpdateTimeLabels();
        await Capture("expanded-error-demo", 380, 300);
        if (!footer.Text.Contains("이전 조회 결과")) throw new Exception("Stale data status missing");
        hadError = false; UpdateTimeLabels();
        snapshot = new UsageSnapshot(new List<LimitWindow> { new("주간 한도", 31, now.AddDays(5).AddHours(2).AddSeconds(30).ToUnixTimeSeconds()) }, "demo", now);
        RenderSnapshot(snapshot);
        await Capture("weekly-only-demo", 270, 160); CheckQuotaLayout(WidgetLayout.Standard);
        if (rows.Children.OfType<Grid>().Count() != 1) throw new Exception("Missing daily quota must not be fabricated");
        snapshot = snapshot with { Windows = new List<LimitWindow> { new("일간 한도", 28, now.AddHours(2).AddMinutes(18).ToUnixTimeSeconds()), new("주간 한도", 31, now.AddDays(5).AddHours(2).AddSeconds(30).ToUnixTimeSeconds()) } };
        RenderSnapshot(snapshot);
        TogglePin(); if (!Topmost) throw new Exception("Pin did not enable Topmost");
        TogglePin(); if (Topmost) throw new Exception("Pin did not disable Topmost");
        ApplyTransparency(80);
        if (((SolidColorBrush)surface.Background).Color.A != 51) throw new Exception("Opacity mismatch");
        await Capture("expanded-transparent-demo", 380, 300); CheckQuotaLayout(WidgetLayout.Expanded);
        File.WriteAllText(System.IO.Path.Combine(folder, "ui-check.json"), JsonSerializer.Serialize(new { pinToggle = true, transparency80 = true, sizesRendered = rendered, essentialLabelsAlwaysVisible = true, expandedDetails = true, staleDataStatus = true, singleQuotaSupported = true }));
        Close();
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(UserSettings settings, Action<int> previewTransparency, Action<int, int, bool, string?> save)
    {
        Title = "Codex Peek 설정"; Width = 365; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(246, 249, 243));
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); FontSize = 12;
        var stack = new StackPanel { Margin = new Thickness(22) }; Content = stack;
        stack.Children.Add(WidgetWindow.Label("나에게 맞는 작은 창", 19, weight: FontWeights.SemiBold));
        stack.Children.Add(new TextBlock { Text = "사용량은 설정한 간격과 새로고침으로 갱신합니다.", FontSize = 11, Margin = new Thickness(0, 7, 0, 19) });
        stack.Children.Add(WidgetWindow.Label("자동 업데이트 간격 · 분"));
        var interval = new TextBox { Text = settings.IntervalMinutes.ToString(), Margin = new Thickness(0, 6, 0, 3), Padding = new Thickness(7), MaxLength = 4 };
        stack.Children.Add(interval); stack.Children.Add(new TextBlock { Text = "1~1,440분 · 기본 30분", FontSize = 10, Foreground = Brushes.Gray });
        var opacityLabel = WidgetWindow.Label($"배경 투명도 · {settings.Transparency}%"); opacityLabel.Margin = new Thickness(0, 16, 0, 5); stack.Children.Add(opacityLabel);
        var slider = new Slider { Minimum = 0, Maximum = 80, Value = settings.Transparency, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, _) => { opacityLabel.Text = $"배경 투명도 · {(int)slider.Value}%"; previewTransparency((int)slider.Value); }; stack.Children.Add(slider);
        var startup = new CheckBox { Content = "Windows 시작 시 실행", IsChecked = settings.StartWithWindows, Margin = new Thickness(0, 16, 0, 15) }; stack.Children.Add(startup);
        stack.Children.Add(WidgetWindow.Label("Codex 실행 파일 · 비우면 자동 검색", 11));
        var pathGrid = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var browse = new Button { Content = "찾기", Width = 43, Margin = new Thickness(5, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); pathGrid.Children.Add(browse);
        var path = new TextBox { Text = settings.CodexPath ?? "", Padding = new Thickness(4), ToolTip = "codex.exe 경로" }; pathGrid.Children.Add(path); stack.Children.Add(pathGrid);
        browse.Click += (_, _) => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex 실행 파일 (*.exe)|*.exe", Title = "codex.exe 선택" }; if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName; };
        var error = new TextBlock { Foreground = Brushes.Firebrick, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }; stack.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "취소", Width = 65, Height = 29, Margin = new Thickness(0, 0, 8, 0), IsCancel = true }; cancel.Click += (_, _) => Close();
        var apply = new Button { Content = "저장", Width = 65, Height = 29, IsDefault = true }; buttons.Children.Add(cancel); buttons.Children.Add(apply); stack.Children.Add(buttons);
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(interval.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > 1440) { error.Text = "갱신 간격은 1~1,440 사이의 정수로 입력하세요."; return; }
            var selected = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim().Trim('"');
            if (selected is not null && (!File.Exists(selected) || !selected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) { error.Text = "존재하는 codex.exe 파일을 선택하세요."; return; }
            try { save(value, (int)slider.Value, startup.IsChecked == true, selected); Close(); }
            catch { error.Text = "설정을 저장할 수 없습니다. Windows 권한을 확인하세요."; }
        };
    }
}
