using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace DragonQuotaWidget;

public partial class MainWindow : Window
{
    private enum BubbleVisual { Normal, Reset, Activity }

    private const double FullBaseWidth = 390;
    private const double FullBaseHeight = 440;
    private const double ArtBaseWidth = 268;
    private const double ArtBaseHeight = 300;
    private const int FiveHourWindowMinutes = 300;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;
    private readonly CodexUsageReader _codexReader = new();
    private readonly AntigravityUsageReader _agyReader = new();
    private readonly DoubaoUsageReader _doubaoReader = new();
    private readonly CodexActivityMonitor _activityMonitor = new();

    private UsageSource SelectedSource
    {
        get
        {
            if (Application.Current.Properties["ForceUsageSource"] is UsageSource previewSource) return previewSource;
            return _settings.UsageSource;
        }
    }
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _activityGate = new(1, 1);
    private readonly DispatcherTimer _activityTimer;
    private readonly DispatcherTimer _bubbleTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _infoPanelTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly bool _forceInfoPanelPreview;
    private readonly bool _forceCharacterOnlyPreview;
    private readonly bool _forceInteractionBubblePreview;
    private readonly MediaPlayer _pressAudio = new();
    private readonly MediaPlayer _releaseAudio = new();
    private UsageSnapshot? _codexSnapshot;
    private UsageSnapshot? _agySnapshot;
    private UsageSnapshot? _doubaoSnapshot;
    private UsageSnapshot? SelectedSnapshot => SelectedSource switch
    {
        UsageSource.Agy => _agySnapshot,
        UsageSource.Doubao => _doubaoSnapshot,
        _ => _codexSnapshot
    };
    private bool _dragging;
    private bool _temporaryInfoPanelVisible;
    private bool _activityInitialized;
    private bool _positionInitialized;
    private bool _resetBubblePinned;
    private bool _codexIsWorking;
    private string _latestActivityStatus = "正在处理任务…";
    private BubbleVisual? _visibleBubbleVisual;
    private DateTimeOffset _interactionLockedUntil;
    private long _lastActivityRevision;
    private long _lastCompletionRevision;
    private WidgetMode _mode;
    private bool _forceExit;
    private bool _minimizeBalloonShown;

    public MainWindow()
    {
        InitializeComponent();
        _forceInfoPanelPreview = Application.Current.Properties["ForceInfoPanelPreview"] is true;
        _forceCharacterOnlyPreview = Application.Current.Properties["ForceCharacterOnlyPreview"] is true;
        _forceInteractionBubblePreview = Application.Current.Properties["ForceInteractionBubblePreview"] is true;
        _mode = Application.Current.Properties["ForceWidgetMode"] is WidgetMode previewMode ? previewMode : _settings.Mode;
        _notifyIcon = CreateNotifyIcon();
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _activityTimer.Tick += async (_, _) => await PollCodexActivityAsync();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(false);
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateQuotaCountdown();
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _bubbleTimer.Tick += (_, _) =>
        {
            _resetBubblePinned = false;
            HideInteractionBubble(restoreActivityAfter: true);
        };
        _infoPanelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.InfoPanelDisplaySeconds) };
        _infoPanelTimer.Tick += (_, _) => HideTemporaryInfoPanel();
        ConfigureSounds();
    }

    private WinForms.NotifyIcon CreateNotifyIcon()
    {
        var icon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "傻龙插件",
            Visible = false
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示挂件", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("彻底退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return icon;
    }

    private bool IsInfoPanelVisible => _forceInfoPanelPreview ||
        (!_forceCharacterOnlyPreview && (_settings.PinInfoPanel || _temporaryInfoPanelVisible));

    private void ApplySettings(bool persist = true)
    {
        Topmost = _settings.AlwaysOnTop;
        DataPanel.BeginAnimation(OpacityProperty, null);
        DataPanel.Visibility = IsInfoPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Opacity = IsInfoPanelVisible ? 1 : 0;
        DragonHost.Cursor = _settings.LockPosition ? Cursors.Arrow : Cursors.Hand;
        _notifyIcon.Visible = _settings.MinimizeOnClose && Application.Current.Properties["RenderPreviewPath"] is not string;
        StartupRegistration.SetEnabled(_settings.StartWithCodex);
        ConfigureSounds();
        ApplyScale(_settings.Scale, persist: false);
        UpdateModeButtons();
        RenderSnapshot();
        if (!_settings.ShowCodexActivityBubble && _visibleBubbleVisual == BubbleVisual.Activity)
            HideInteractionBubble(immediate: true);
        else
            RestoreActivityBubbleIfNeeded();
        if (persist) _settings.Save();
    }

    private void RestoreFromTray()
    {
        if (Application.Current is App app) app.ResumeWatcherVisibility();
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        _activityTimer.Start();
        _countdownTimer.Start();
        _refreshTimer.Start();
        ClampToWorkArea();
        UpdateDragonMirror();
        Activate();
    }

    public void ActivateFromExternalRequest()
    {
        RestoreFromTray();
        ShowInfoPanelTemporarily();
        _ = RefreshAsync(true);
    }

    private void ExitApplication()
    {
        _forceExit = true;
        if (Application.Current is App app) app.ExitCompletely();
        else Close();
    }

    public async void SetCodexLifecycleVisible(bool visible)
    {
        if (visible)
        {
            if (!IsVisible) Show();
            _activityTimer.Start();
            _countdownTimer.Start();
            _refreshTimer.Start();
            ClampToWorkArea();
            UpdateDragonMirror();
            await RefreshAsync(false);
        }
        else
        {
            // Codex detection can briefly disappear while its desktop window
            // is minimized, recreated, or updated. The widget is independent
            // once launched, so a host lifecycle change must not hide it.
            RestoreActivityBubbleIfNeeded();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings(persist: false);
        if (_settings.Left is not null && _settings.Top is not null)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
            ClampToWorkArea();
        }
        else
        {
            PositionAtWorkAreaCorner();
        }

        _positionInitialized = true;
        UpdateDragonMirror();

        UpdateModeButtons();
        _activityTimer.Start();
        _countdownTimer.Start();
        _refreshTimer.Start();
        await RefreshAsync(true);
        if (Application.Current.Properties["RenderPreviewPath"] is string previewPath)
        {
            if (_forceInteractionBubblePreview)
            {
                var previewText = Application.Current.Properties["InteractionBubblePreviewText"] as string ?? "好模型";
                ShowInteractionBubble(previewText, autoHide: false, animate: false);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SavePreview(previewPath);
            _forceExit = true;
            Close();
        }
    }

    private async Task RefreshAsync(bool manual)
    {
        // A user-triggered refresh must never be discarded. The lifecycle and
        // timer refreshes may already be reading a large session log, so wait
        // for that read to finish and then take a fresh snapshot. Background
        // refreshes can still be skipped to avoid redundant full scans.
        if (manual)
        {
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var source = SelectedSource;
            var sourceLabel = FormatSource(source);
            if (manual)
            {
                StatusText.Text = $"正在刷新本地 {sourceLabel} 数据…";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 203, 107));
            }
            var snapshot = await Task.Run(() => source switch
            {
                UsageSource.Agy => _agyReader.ReadSnapshot(),
                UsageSource.Doubao => _doubaoReader.ReadSnapshot(forceRefresh: manual),
                _ => _codexReader.ReadSnapshot()
            });
            if (source == UsageSource.Agy) _agySnapshot = snapshot;
            else if (source == UsageSource.Doubao) _doubaoSnapshot = snapshot;
            else _codexSnapshot = snapshot;

            if (source == SelectedSource)
                RenderSnapshot();
        }
        catch (Exception ex)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 128, 151));
            StatusText.Text = $"读取失败：{ex.Message}";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PollCodexActivityAsync()
    {
        if (!await _activityGate.WaitAsync(0)) return;
        try
        {
            var activity = await Task.Run(_activityMonitor.Poll);
            _codexIsWorking = activity.IsWorking;
            _latestActivityStatus = activity.StatusText;
            if (!_activityInitialized)
            {
                _activityInitialized = true;
                _lastActivityRevision = activity.Revision;
                _lastCompletionRevision = activity.CompletionRevision;
                if (activity.IsWorking && _settings.ShowCodexActivityBubble)
                {
                    ShowActivityBubble(activity.StatusText);
                }
                return;
            }

            var completed = activity.CompletionRevision > _lastCompletionRevision;
            var changed = activity.Revision > _lastActivityRevision;
            _lastActivityRevision = activity.Revision;
            _lastCompletionRevision = activity.CompletionRevision;

            if (completed)
            {
                ShowInfoPanelTemporarily();
                await RefreshAsync(false);
            }
            else if (changed && activity.IsWorking && _settings.ShowCodexActivityBubble)
            {
                ShowActivityBubble(activity.StatusText);
            }
            else if (!activity.IsWorking && _visibleBubbleVisual == BubbleVisual.Activity)
            {
                HideInteractionBubble(immediate: true);
            }
        }
        finally
        {
            _activityGate.Release();
        }
    }

    private void RenderSnapshot()
    {
        var snapshot = SelectedSnapshot;
        var sourceLabel = FormatSource(SelectedSource);
        if (snapshot is null)
        {
            TitleText.Text = $"{sourceLabel} 数据";
            MainValueText.Text = "--";
            MainLabelText.Text = "正在读取";
            MainSubText.Text = $"等待 {sourceLabel} 数据刷新";
            UsageProgress.Value = 0;
            Detail1Label.Text = "输入";
            Detail2Label.Text = "输出";
            Detail3Label.Text = "缓存命中";
            Detail1Value.Text = Detail2Value.Text = Detail3Value.Text = "--";
            StatusText.Text = $"正在读取本地 {sourceLabel} 数据…";
            return;
        }

        if (SelectedSource == UsageSource.Doubao && _mode is not (WidgetMode.Quota or WidgetMode.FiveHourQuota))
        {
            RenderDoubaoQuotaOnlyState();
        }
        else if (_mode == WidgetMode.Quota)
        {
            var title = SelectedSource == UsageSource.Doubao ? "近 7 天额度" : "每周额度";
            var remaining = SelectedSource == UsageSource.Doubao ? "近 7 天剩余额度" : "每周剩余额度";
            RenderQuota(snapshot, WeeklyWindowMinutes, title, remaining);
        }
        else if (_mode == WidgetMode.FiveHourQuota)
        {
            var title = SelectedSource == UsageSource.Doubao ? "当前时段额度" : "5h 额度";
            var remaining = SelectedSource == UsageSource.Doubao ? "当前时段剩余额度" : "5h 剩余额度";
            RenderQuota(snapshot, FiveHourWindowMinutes, title, remaining);
        }
        else if (_mode == WidgetMode.Summary)
        {
            RenderSummaryPeriod(snapshot);
        }
        else if (_mode == WidgetMode.Today)
        {
            RenderTokenPeriod(snapshot);
        }
        else
        {
            RenderConversation(snapshot);
        }

        var warning = string.IsNullOrWhiteSpace(snapshot.Warning) ? string.Empty : $" · {snapshot.Warning}";
        var staleQuota = _mode is WidgetMode.Quota or WidgetMode.FiveHourQuota && snapshot.RateLimits?.IsStale == true;
        StatusText.Text = staleQuota
            ? $"本地 {sourceLabel} 缓存 {snapshot.ReadAt:HH:mm:ss}{warning}"
            : $"本地 {sourceLabel} 刷新 {snapshot.ReadAt:HH:mm:ss}{warning}";
        StatusDot.Fill = new SolidColorBrush(staleQuota
            ? Color.FromRgb(255, 203, 107)
            : Color.FromRgb(138, 230, 192));
    }

    private void RenderQuota(UsageSnapshot snapshot, int targetWindowMinutes, string title, string remainingLabel)
    {
        var source = SelectedSource;
        var sourceLabel = FormatSource(source);
        TitleText.Text = $"{sourceLabel} {title}";
        var window = SelectRateWindow(snapshot.RateLimits, targetWindowMinutes);
        if (window is null)
        {
            MainValueText.Text = "--";
            MainLabelText.Text = $"暂无 {FormatWindow(targetWindowMinutes)}额度快照";
            MainSubText.Text = source switch
            {
                UsageSource.Agy => "暂无可用 AGY 额度快照",
                UsageSource.Doubao => "请启动并登录豆包电脑版后刷新",
                _ => "产生一次 Codex 响应后再刷新"
            };
            UsageProgress.Value = 0;
            Detail1Label.Text = "已使用";
            Detail1Value.Text = "--";
            Detail2Label.Text = "重置倒计时";
            Detail2Value.Text = "--";
            Detail3Label.Text = "重置";
            Detail3Value.Text = "--";
            return;
        }

        var remaining = Math.Clamp(100d - window.UsedPercent, 0d, 100d);
        MainValueText.Text = FormatRemainingPercent(window, remaining);
        MainLabelText.Text = remainingLabel;
        MainSubText.Text = snapshot.RateLimits?.IsStale == true
            ? $"{sourceLabel} 旧缓存 · {FormatWindow(window.WindowMinutes)}"
            : source == UsageSource.Doubao
                ? $"豆包官方额度 · {FormatWindow(window.WindowMinutes)}"
                : $"{sourceLabel} 汇总 · {FormatWindow(window.WindowMinutes)}";
        UsageProgress.Value = remaining;
        Detail1Label.Text = "已使用";
        Detail1Value.Text = window.UsedPercentText ?? $"{window.UsedPercent:0.#}%";
        Detail2Label.Text = "重置倒计时";
        Detail2Value.Text = FormatResetCountdown(window.ResetsAt);
        Detail3Label.Text = "重置时间";
        Detail3Value.Text = FormatReset(window.ResetsAt);
    }

    private static RateWindow? SelectRateWindow(RateLimitSnapshot? limits, int targetWindowMinutes)
    {
        if (limits is null) return null;
        if (limits.Primary?.WindowMinutes == targetWindowMinutes) return limits.Primary;
        if (limits.Secondary?.WindowMinutes == targetWindowMinutes) return limits.Secondary;
        return null;
    }

    private void RenderDoubaoQuotaOnlyState()
    {
        TitleText.Text = "豆包额度";
        MainValueText.Text = "--";
        MainLabelText.Text = "豆包仅提供额度数据";
        MainSubText.Text = "请选择“当前”或“近7天”";
        UsageProgress.Value = 0;
        Detail1Label.Text = "当前时段";
        Detail2Label.Text = "近 7 天";
        Detail3Label.Text = "数据来源";
        Detail1Value.Text = Detail2Value.Text = "--";
        Detail3Value.Text = "豆包客户端";
    }

    private void RenderTokenPeriod(UsageSnapshot snapshot)
    {
        var source = SelectedSource;
        var sourceLabel = FormatSource(source);
        var isRolling = _settings.TokenTimeRange == TokenTimeRange.Last24Hours;
        var period = isRolling ? snapshot.Last24Hours : snapshot.Today;

        TitleText.Text = isRolling ? $"{sourceLabel} 24h Token" : $"{sourceLabel} 今日 Token";

        if (source == UsageSource.Agy)
        {
            var agy = period.Agy;
            MainValueText.Text = FormatTokens(agy.TotalTokens);
            MainLabelText.Text = "输入 + 输出";
            MainSubText.Text = $"AGY 会话 · 缓存命中 {agy.CacheHitRate * 100d:0.0}%";
            UsageProgress.Value = agy.CacheHitRate * 100d;
            Detail1Label.Text = "输入";
            Detail1Value.Text = FormatTokens(agy.InputTokens);
            Detail2Label.Text = "输出";
            Detail2Value.Text = FormatTokens(agy.OutputTokens);
            Detail3Label.Text = "缓存命中";
            Detail3Value.Text = $"{agy.CacheHitRate * 100d:0.0}%";
        }
        else
        {
            var total = period.Codex + period.Work;
            MainValueText.Text = FormatTokens(total.TotalTokens);
            MainLabelText.Text = "输入 + 输出";
            MainSubText.Text = $"Work {FormatTokens(period.Work.TotalTokens)} · Codex {FormatTokens(period.Codex.TotalTokens)}";
            UsageProgress.Value = total.CacheHitRate * 100d;
            Detail1Label.Text = "输入";
            Detail1Value.Text = FormatTokens(total.InputTokens);
            Detail2Label.Text = "输出";
            Detail2Value.Text = FormatTokens(total.OutputTokens);
            Detail3Label.Text = "缓存命中";
            Detail3Value.Text = $"{total.CacheHitRate * 100d:0.0}%";
        }
    }

    private void RenderSummaryPeriod(UsageSnapshot snapshot)
    {
        var source = SelectedSource;
        var sourceLabel = FormatSource(source);
        var (period, baseTitle) = _settings.SummaryTimeRange switch
        {
            SummaryTimeRange.Last30Days => (snapshot.Last30Days, "30天 Token"),
            SummaryTimeRange.AllTime => (snapshot.AllTime, "总 Token"),
            _ => (snapshot.Last7Days, "7天 Token")
        };

        TitleText.Text = $"{sourceLabel} {baseTitle}";

        if (source == UsageSource.Agy)
        {
            var agy = period.Agy;
            MainValueText.Text = FormatTokens(agy.TotalTokens);
            MainLabelText.Text = "输入 + 输出";
            MainSubText.Text = $"AGY 会话 · 缓存命中 {agy.CacheHitRate * 100d:0.0}%";
            UsageProgress.Value = agy.CacheHitRate * 100d;
            Detail1Label.Text = "输入";
            Detail1Value.Text = FormatTokens(agy.InputTokens);
            Detail2Label.Text = "输出";
            Detail2Value.Text = FormatTokens(agy.OutputTokens);
            Detail3Label.Text = "缓存命中";
            Detail3Value.Text = $"{agy.CacheHitRate * 100d:0.0}%";
        }
        else
        {
            var total = period.Codex + period.Work;
            MainValueText.Text = FormatTokens(total.TotalTokens);
            MainLabelText.Text = "输入 + 输出";
            MainSubText.Text = $"Work {FormatTokens(period.Work.TotalTokens)} · Codex {FormatTokens(period.Codex.TotalTokens)}";
            UsageProgress.Value = total.CacheHitRate * 100d;
            Detail1Label.Text = "输入";
            Detail1Value.Text = FormatTokens(total.InputTokens);
            Detail2Label.Text = "输出";
            Detail2Value.Text = FormatTokens(total.OutputTokens);
            Detail3Label.Text = "缓存命中";
            Detail3Value.Text = $"{total.CacheHitRate * 100d:0.0}%";
        }
    }

    private void RenderConversation(UsageSnapshot snapshot)
    {
        var source = SelectedSource;
        var sourceLabel = FormatSource(source);
        var conversation = snapshot.CurrentConversation;
        if (conversation is null)
        {
            TitleText.Text = $"{sourceLabel} 本轮 Token";
            MainValueText.Text = "--";
            MainLabelText.Text = "暂无活动会话";
            MainSubText.Text = source == UsageSource.Agy ? "等待 AGY 写入会话事件" : "等待 Codex 写入会话事件";
            UsageProgress.Value = 0;
            Detail1Label.Text = "输入";
            Detail2Label.Text = "输出";
            Detail3Label.Text = "缓存命中";
            Detail1Value.Text = Detail2Value.Text = Detail3Value.Text = "--";
            return;
        }

        var usage = conversation.Tokens;
        TitleText.Text = $"{sourceLabel} 本轮 Token";
        MainValueText.Text = FormatTokens(usage.TotalTokens);
        MainLabelText.Text = "输入 + 输出";
        var surfaceLabel = source == UsageSource.Agy ? "AGY" : conversation.Surface.ToString();
        MainSubText.Text = $"{surfaceLabel} 模式 · {conversation.StartedAt.ToLocalTime():MM-dd HH:mm} 开始";
        UsageProgress.Value = usage.CacheHitRate * 100d;
        Detail1Label.Text = "输入";
        Detail1Value.Text = FormatTokens(usage.InputTokens);
        Detail2Label.Text = "输出";
        Detail2Value.Text = FormatTokens(usage.OutputTokens);
        Detail3Label.Text = "缓存命中";
        Detail3Value.Text = $"{usage.CacheHitRate * 100d:0.0}%";
    }

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string FormatSource(UsageSource source) => source switch
    {
        UsageSource.Agy => "AGY",
        UsageSource.Doubao => "豆包",
        _ => "Codex"
    };

    private static string FormatRemainingPercent(RateWindow window, double remaining)
    {
        var text = window.UsedPercentText;
        if (text is { Length: > 2 } && text[0] == '<' && text[^1] == '%' &&
            double.TryParse(text[1..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var upperBound))
        {
            return $">{Math.Clamp(100d - upperBound, 0d, 100d):0.#}%";
        }
        return $"{remaining:0.#}%";
    }

    private static string FormatWindow(int? minutes)
    {
        if (minutes is null or <= 0) return "未知周期";
        if (minutes.Value % 1440 == 0) return $"{minutes.Value / 1440} 天";
        if (minutes.Value % 60 == 0) return $"{minutes.Value / 60} 小时";
        return $"{minutes.Value} 分钟";
    }

    private static string FormatReset(DateTimeOffset? reset)
    {
        if (reset is null) return "--";
        var local = reset.Value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
    }

    private static string FormatResetCountdown(DateTimeOffset? reset)
    {
        if (reset is null) return "--";
        var remaining = reset.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "等待刷新";
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}天 {remaining.Hours:00}:{remaining.Minutes:00}";
        }

        return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void UpdateQuotaCountdown()
    {
        if (_mode is not (WidgetMode.Quota or WidgetMode.FiveHourQuota) || SelectedSnapshot?.RateLimits is not { } limit) return;
        var targetWindowMinutes = _mode == WidgetMode.FiveHourQuota ? FiveHourWindowMinutes : WeeklyWindowMinutes;
        var window = SelectRateWindow(limit, targetWindowMinutes);
        if (window is null) return;
        Detail2Value.Text = FormatResetCountdown(window.ResetsAt);
    }

    private void SetMode(WidgetMode mode)
    {
        _mode = mode;
        _settings.Mode = mode;
        _settings.Save();
        UpdateModeButtons();
        RenderSnapshot();
    }

    private void UpdateModeButtons()
    {
        var active = new SolidColorBrush(Color.FromArgb(90, 190, 179, 255));
        var inactive = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        QuotaModeButton.Content = SelectedSource == UsageSource.Doubao ? "近7天" : "每周";
        FiveHourQuotaModeButton.Content = SelectedSource == UsageSource.Doubao ? "当前" : "5h";
        QuotaModeButton.Background = _mode == WidgetMode.Quota ? active : inactive;
        FiveHourQuotaModeButton.Background = _mode == WidgetMode.FiveHourQuota ? active : inactive;
        SummaryModeButton.Background = _mode == WidgetMode.Summary ? active : inactive;
        TodayModeButton.Background = _mode == WidgetMode.Today ? active : inactive;
        ConversationModeButton.Background = _mode == WidgetMode.Conversation ? active : inactive;
        QuotaModeButton.Foreground = _mode == WidgetMode.Quota ? Brushes.White : new SolidColorBrush(Color.FromRgb(185, 179, 214));
        FiveHourQuotaModeButton.Foreground = _mode == WidgetMode.FiveHourQuota ? Brushes.White : new SolidColorBrush(Color.FromRgb(185, 179, 214));
        SummaryModeButton.Foreground = _mode == WidgetMode.Summary ? Brushes.White : new SolidColorBrush(Color.FromRgb(185, 179, 214));
        TodayModeButton.Foreground = _mode == WidgetMode.Today ? Brushes.White : new SolidColorBrush(Color.FromRgb(185, 179, 214));
        ConversationModeButton.Foreground = _mode == WidgetMode.Conversation ? Brushes.White : new SolidColorBrush(Color.FromRgb(185, 179, 214));
        var summaryLabel = _settings.SummaryTimeRange switch
        {
            SummaryTimeRange.Last30Days => "30 天",
            SummaryTimeRange.AllTime => "总计",
            _ => "7 天"
        };
        SummaryModeButton.Content = summaryLabel;
        SummaryMenuItem.Header = $"{summaryLabel} Token 模式";
        var tokenLabel = _settings.TokenTimeRange == TokenTimeRange.Last24Hours ? "24h Token" : "今日 Token";
        TodayModeButton.Content = _settings.TokenTimeRange == TokenTimeRange.Last24Hours ? "24h" : "今日";
        TodayMenuItem.Header = $"{tokenLabel} 模式";
        PinInfoPanelButton.Content = _settings.PinInfoPanel ? "◆" : "◇";
        PinInfoPanelButton.ToolTip = _settings.PinInfoPanel
            ? "取消固定额度提示框，恢复自动淡出"
            : "固定额度提示框，使其不再自动淡出";
        SourceToggleButton.Content = FormatSource(SelectedSource);
        SourceToggleButton.ToolTip = SelectedSource switch
        {
            UsageSource.Codex => "当前显示 Codex；点击切换到 AGY",
            UsageSource.Agy => "当前显示 AGY；点击切换到豆包",
            _ => "当前显示豆包；点击切换到 Codex"
        };
        CodexQuotaModeMenuItem.IsChecked = _settings.LeftClickMode == LeftClickDisplayMode.CodexQuota;
        AgyQuotaModeMenuItem.IsChecked = _settings.LeftClickMode == LeftClickDisplayMode.AgyQuota;
        DoubaoQuotaModeMenuItem.IsChecked = _settings.LeftClickMode == LeftClickDisplayMode.DoubaoQuota;
        InteractionDisplayModeMenuItem.IsChecked = _settings.LeftClickMode == LeftClickDisplayMode.Interaction;
        LockPositionMenuItem.IsChecked = _settings.LockPosition;
        PinInfoPanelMenuItem.IsChecked = _settings.PinInfoPanel;
    }

    private void TogglePinInfoPanel()
    {
        _settings.PinInfoPanel = !_settings.PinInfoPanel;
        _settings.Save();
        UpdateModeButtons();
        if (_settings.PinInfoPanel)
        {
            ShowInfoPanelTemporarily();
        }
        else
        {
            _temporaryInfoPanelVisible = true;
            HideTemporaryInfoPanel();
        }
    }

    private void HideInfoPanel()
    {
        _settings.PinInfoPanel = false;
        _temporaryInfoPanelVisible = false;
        _infoPanelTimer.Stop();
        ApplySettings();
        RestoreActivityBubbleIfNeeded();
    }

    private void SetLeftClickMode(LeftClickDisplayMode mode)
    {
        _settings.LeftClickMode = mode;
        if (mode == LeftClickDisplayMode.CodexQuota)
        {
            _settings.UsageSource = UsageSource.Codex;
        }
        else if (mode == LeftClickDisplayMode.AgyQuota)
        {
            _settings.UsageSource = UsageSource.Agy;
        }
        else if (mode == LeftClickDisplayMode.DoubaoQuota)
        {
            _settings.UsageSource = UsageSource.Doubao;
            _mode = WidgetMode.FiveHourQuota;
            _settings.Mode = _mode;
        }
        _settings.Save();
        UpdateModeButtons();
        RenderSnapshot();
        _ = RefreshAsync(true);
    }

    private void ToggleUsageSource()
    {
        if (Application.Current.Properties["ForceUsageSource"] is UsageSource) return;
        _settings.UsageSource = NextUsageSource(SelectedSource);
        if (_settings.UsageSource == UsageSource.Doubao)
        {
            _mode = WidgetMode.FiveHourQuota;
            _settings.Mode = _mode;
        }
        if (_settings.LeftClickMode is LeftClickDisplayMode.CodexQuota or LeftClickDisplayMode.AgyQuota or LeftClickDisplayMode.DoubaoQuota)
        {
            _settings.LeftClickMode = _settings.UsageSource switch
            {
                UsageSource.Agy => LeftClickDisplayMode.AgyQuota,
                UsageSource.Doubao => LeftClickDisplayMode.DoubaoQuota,
                _ => LeftClickDisplayMode.CodexQuota
            };
        }
        _settings.Save();
        UpdateModeButtons();
        RenderSnapshot();
        ShowInfoPanelTemporarily();
        _ = RefreshAsync(true);
    }

    public static UsageSource NextUsageSource(UsageSource source) => source switch
    {
        UsageSource.Codex => UsageSource.Agy,
        UsageSource.Agy => UsageSource.Doubao,
        _ => UsageSource.Codex
    };

    private void TogglePositionLock()
    {
        _settings.LockPosition = !_settings.LockPosition;
        ApplySettings();
    }

    private void OpenSettings()
    {
        RestoreFromTray();
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        dialog.ApplyTo(_settings);
        ApplySettings();
        _ = RefreshAsync(true);
    }

    private void DragonHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DateTimeOffset.Now < _interactionLockedUntil)
        {
            e.Handled = true;
            return;
        }

        SuspendActivityBubbleForInteraction();
        PlaySound(_pressAudio);
        if (_settings.LockPosition)
        {
            CompleteDragonInteraction();
            e.Handled = true;
            return;
        }

        _dragging = true;
        DragonHost.Cursor = Cursors.Hand;
        Mouse.OverrideCursor = Cursors.Hand;
        try { DragMove(); }
        finally
        {
            Mouse.OverrideCursor = null;
            _dragging = false;
            ClampToWorkArea();
            UpdateDragonMirror();
            PersistPosition();
            UpdateModeButtons();
            PlayReleaseBounce();
            CompleteDragonInteraction();
        }
    }

    private void CompleteDragonInteraction()
    {
        PlaySound(_releaseAudio);
        if (_settings.LeftClickMode is LeftClickDisplayMode.CodexQuota or LeftClickDisplayMode.AgyQuota or LeftClickDisplayMode.DoubaoQuota)
        {
            ShowInfoPanelTemporarily();
            _ = RefreshAsync(true);
        }
        else
        {
            ShowInteractionBubble();
            _ = RefreshAsync(false);
        }
    }

    private void SuspendActivityBubbleForInteraction()
    {
        if (_visibleBubbleVisual == BubbleVisual.Activity)
        {
            HideInteractionBubble(immediate: true);
        }
    }

    private void PlayReleaseBounce()
    {
        var bounce = new BackEase { Amplitude = 0.42, EasingMode = EasingMode.EaseOut };
        DragonHop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-15, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = bounce });
        DragonSquash.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = bounce });
        DragonSquash.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = bounce });
    }

    private void ShowInteractionBubble(string? message = null, bool autoHide = true, bool animate = true)
    {
        if (_resetBubblePinned && DateTimeOffset.Now < _interactionLockedUntil) return;
        HideTemporaryInfoPanelImmediately();
        if (message is null)
        {
            var roll = Random.Shared.Next(100);
            message = roll < 2 ? "今日reset" : Random.Shared.Next(2) == 0 ? "好模型" : "臭模型";
        }

        var isReset = message == "今日reset";
        if (isReset)
        {
            var lockDuration = TimeSpan.FromSeconds(_settings.ResetInteractionLockSeconds);
            _interactionLockedUntil = DateTimeOffset.Now + lockDuration;
            _resetBubblePinned = true;
            ShowBubble(message, BubbleVisual.Reset, autoHide, animate, lockDuration);
        }
        else
        {
            ShowBubble(message, BubbleVisual.Normal, autoHide, animate, TimeSpan.FromMilliseconds(2600));
        }
    }

    private void ShowActivityBubble(string message)
    {
        if (!_settings.ShowCodexActivityBubble || !_codexIsWorking || IsInfoPanelVisible || _dragging ||
            _visibleBubbleVisual is BubbleVisual.Normal or BubbleVisual.Reset ||
            (_resetBubblePinned && DateTimeOffset.Now < _interactionLockedUntil)) return;
        ShowBubble(message, BubbleVisual.Activity, autoHide: false, animate: true, TimeSpan.Zero);
    }

    private void RestoreActivityBubbleIfNeeded()
    {
        if (_codexIsWorking && _settings.ShowCodexActivityBubble && !IsInfoPanelVisible && !_dragging &&
            _visibleBubbleVisual is null)
        {
            ShowActivityBubble(_latestActivityStatus);
        }
    }

    private void ShowBubble(string message, BubbleVisual visual, bool autoHide, bool animate, TimeSpan duration)
    {
        _bubbleTimer.Stop();
        _visibleBubbleVisual = visual;
        ApplyBubbleVisual(visual);
        InteractionBubbleText.Text = message;
        InteractionBubble.Visibility = Visibility.Visible;
        InteractionBubble.BeginAnimation(OpacityProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (animate)
        {
            InteractionBubble.Opacity = 0;
            BubbleScale.ScaleX = 0.82;
            BubbleScale.ScaleY = 0.82;
            var ease = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
            InteractionBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else
        {
            InteractionBubble.Opacity = 1;
            BubbleScale.ScaleX = 1;
            BubbleScale.ScaleY = 1;
        }

        if (autoHide)
        {
            _bubbleTimer.Interval = duration;
            _bubbleTimer.Start();
        }
    }

    private void ApplyBubbleVisual(BubbleVisual visual)
    {
        InteractionBubble.Width = visual switch { BubbleVisual.Reset => 244, BubbleVisual.Activity => 236, _ => 170 };
        InteractionBubble.Height = visual switch { BubbleVisual.Reset => 104, BubbleVisual.Activity => 94, _ => 78 };
        BubbleBorder.CornerRadius = new CornerRadius(visual == BubbleVisual.Reset ? 30 : 24);
        InteractionBubbleText.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        InteractionBubbleText.FontSize = visual switch { BubbleVisual.Reset => 27, BubbleVisual.Activity => 13.5, _ => 15 };
        InteractionBubbleText.FontWeight = visual == BubbleVisual.Reset ? FontWeights.ExtraBold : FontWeights.SemiBold;
        InteractionBubbleText.FontStyle = visual == BubbleVisual.Reset ? FontStyles.Italic : FontStyles.Normal;
        InteractionBubbleText.Effect = visual == BubbleVisual.Reset
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(117, 91, 204), BlurRadius = 7, ShadowDepth = 2, Opacity = 0.65 }
            : null;
        InteractionBubbleText.Foreground = visual == BubbleVisual.Reset
            ? new LinearGradientBrush(Color.FromRgb(116, 83, 213), Color.FromRgb(242, 121, 180), 0)
            : new SolidColorBrush(Color.FromRgb(52, 38, 81));
        BubbleBorder.Background = visual == BubbleVisual.Reset
            ? new LinearGradientBrush(Color.FromRgb(255, 252, 255), Color.FromRgb(239, 230, 255), 90)
            : new SolidColorBrush(Color.FromArgb(249, 255, 255, 255));
        BubbleTail.Fill = BubbleBorder.Background;
    }

    private void HideInteractionBubble(bool immediate = false, bool restoreActivityAfter = false)
    {
        if (_resetBubblePinned && !immediate) return;
        _bubbleTimer.Stop();
        if (InteractionBubble.Visibility != Visibility.Visible) return;
        if (immediate)
        {
            _resetBubblePinned = false;
            InteractionBubble.BeginAnimation(OpacityProperty, null);
            InteractionBubble.Opacity = 0;
            InteractionBubble.Visibility = Visibility.Collapsed;
            _visibleBubbleVisual = null;
            if (restoreActivityAfter) RestoreActivityBubbleIfNeeded();
            return;
        }

        var fade = new DoubleAnimation(InteractionBubble.Opacity, 0, TimeSpan.FromMilliseconds(190));
        fade.Completed += (_, _) =>
        {
            InteractionBubble.Visibility = Visibility.Collapsed;
            _visibleBubbleVisual = null;
            if (restoreActivityAfter) RestoreActivityBubbleIfNeeded();
        };
        InteractionBubble.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowInfoPanelTemporarily()
    {
        if (_forceCharacterOnlyPreview) return;
        HideInteractionBubble(immediate: true);
        var wasVisible = DataPanel.Visibility == Visibility.Visible && DataPanel.Opacity > 0;
        _infoPanelTimer.Stop();
        _temporaryInfoPanelVisible = true;
        DataPanel.Visibility = Visibility.Visible;
        DataPanel.BeginAnimation(OpacityProperty, null);
        ApplyScale(_settings.Scale, persist: false);
        ClampToWorkArea();
        if (wasVisible)
        {
            DataPanel.Opacity = 1;
        }
        else
        {
            DataPanel.Opacity = 0;
            DataPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        if (!_settings.PinInfoPanel)
        {
            _infoPanelTimer.Interval = TimeSpan.FromSeconds(_settings.InfoPanelDisplaySeconds);
            _infoPanelTimer.Start();
        }
    }

    private void HideTemporaryInfoPanelImmediately()
    {
        _infoPanelTimer.Stop();
        if (_settings.PinInfoPanel) return;
        if (!_temporaryInfoPanelVisible) return;
        DataPanel.BeginAnimation(OpacityProperty, null);
        _temporaryInfoPanelVisible = false;
        DataPanel.Opacity = 0;
        DataPanel.Visibility = Visibility.Collapsed;
        ApplyScale(_settings.Scale, persist: false);
        ClampToWorkArea();
    }

    private void HideTemporaryInfoPanel()
    {
        _infoPanelTimer.Stop();
        if (_settings.PinInfoPanel)
        {
            DataPanel.BeginAnimation(OpacityProperty, null);
            DataPanel.Visibility = Visibility.Visible;
            DataPanel.Opacity = 1;
            return;
        }
        if (!_temporaryInfoPanelVisible) return;
        var fade = new DoubleAnimation(DataPanel.Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            _temporaryInfoPanelVisible = false;
            DataPanel.Visibility = Visibility.Collapsed;
            ApplyScale(_settings.Scale, persist: false);
            ClampToWorkArea();
            RestoreActivityBubbleIfNeeded();
        };
        DataPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void ConfigureSounds()
    {
        _pressAudio.Close();
        _releaseAudio.Close();
        var names = _settings.SoundSet == InteractionSoundSet.Effect1
            ? (Press: "D1.mp3", Release: "D2.mp3")
            : (Press: "Ya1.mp3", Release: "Ya2.mp3");
        OpenSound(_pressAudio, names.Press);
        OpenSound(_releaseAudio, names.Release);
    }

    private static void OpenSound(MediaPlayer player, string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", fileName);
        if (System.IO.File.Exists(path)) player.Open(new Uri(path, UriKind.Absolute));
    }

    private void PlaySound(MediaPlayer player)
    {
        if (!_settings.SoundEnabled || _settings.SoundVolume <= 0) return;
        try
        {
            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Volume = _settings.SoundVolume;
            player.Play();
        }
        catch { }
    }

    private void ApplyScale(double scale, bool persist = true)
    {
        scale = Math.Clamp(Math.Round(scale, 1), 0.5, 1.8);
        _settings.Scale = scale;
        var keepDragonAnchor = _positionInitialized &&
            !double.IsNaN(Left) && !double.IsNaN(Top);
        var previousWidth = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
        var previousHeight = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
        var anchorRight = Left + previousWidth;
        var anchorBottom = Top + previousHeight;
        var baseWidth = IsInfoPanelVisible ? FullBaseWidth : ArtBaseWidth;
        var baseHeight = IsInfoPanelVisible ? FullBaseHeight : ArtBaseHeight;
        Width = baseWidth * scale;
        Height = baseHeight * scale;
        RootGrid.LayoutTransform = new ScaleTransform(scale, scale);
        if (keepDragonAnchor)
        {
            Left = anchorRight - Width;
            Top = anchorBottom - Height;
        }
        if (persist) _settings.Save();
        if (!IsLoaded) return;
        if (!double.IsNaN(Left) && !double.IsNaN(Top)) ClampToWorkArea();
        UpdateDragonMirror();
    }

    private void PositionAtWorkAreaCorner()
    {
        var work = GetCurrentWorkArea();
        var windowWidth = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
        var windowHeight = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
        Left = work.Right - windowWidth - 12;
        Top = work.Bottom - windowHeight - 12;
        UpdateDragonMirror();
    }

    private void ClampToWorkArea()
    {
        var work = GetCurrentWorkArea();
        var windowWidth = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
        var windowHeight = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - windowWidth));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - windowHeight));
        UpdateDragonMirror();
    }

    private void UpdateDragonMirror()
    {
        if (double.IsNaN(Left) || double.IsNaN(Width) || Width <= 0) return;
        var work = GetCurrentWorkArea();
        var dragonCenterX = Left + Width - ArtBaseWidth * _settings.Scale / 2d;
        DragonMirror.ScaleX = WidgetPlacement.GetFacingScaleX(dragonCenterX, work.Left, work.Width);
    }

    private Rect GetCurrentWorkArea()
    {
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return SystemParameters.WorkArea;
        var source = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (source is null) return SystemParameters.WorkArea;
        var centerDip = new Point(Left + Math.Max(Width, ActualWidth) / 2d, Top + Math.Max(Height, ActualHeight) / 2d);
        var centerDevice = source.TransformToDevice.Transform(centerDip);
        var screen = WinForms.Screen.FromPoint(new Drawing.Point((int)Math.Round(centerDevice.X), (int)Math.Round(centerDevice.Y)));
        var topLeft = source.TransformFromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = source.TransformFromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void PersistPosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void QuotaModeButton_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Quota);
    private void FiveHourQuotaModeButton_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.FiveHourQuota);
    private void SummaryModeButton_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Summary);
    private void TodayModeButton_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Today);
    private void ConversationModeButton_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Conversation);
    private void QuotaMenuItem_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Quota);
    private void FiveHourQuotaMenuItem_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.FiveHourQuota);
    private void SummaryMenuItem_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Summary);
    private void TodayMenuItem_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Today);
    private void ConversationMenuItem_Click(object sender, RoutedEventArgs e) => SetMode(WidgetMode.Conversation);
    private void CodexQuotaModeMenuItem_Click(object sender, RoutedEventArgs e) => SetLeftClickMode(LeftClickDisplayMode.CodexQuota);
    private void AgyQuotaModeMenuItem_Click(object sender, RoutedEventArgs e) => SetLeftClickMode(LeftClickDisplayMode.AgyQuota);
    private void DoubaoQuotaModeMenuItem_Click(object sender, RoutedEventArgs e) => SetLeftClickMode(LeftClickDisplayMode.DoubaoQuota);
    private void InteractionDisplayModeMenuItem_Click(object sender, RoutedEventArgs e) => SetLeftClickMode(LeftClickDisplayMode.Interaction);
    private void HideInfoButton_Click(object sender, RoutedEventArgs e) => HideInfoPanel();
    private void ShowInfoMenuItem_Click(object sender, RoutedEventArgs e) => ShowInfoPanelTemporarily();
    private void LockPositionMenuItem_Click(object sender, RoutedEventArgs e) => TogglePositionLock();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void SourceToggleButton_Click(object sender, RoutedEventArgs e) => ToggleUsageSource();
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void PinInfoPanelButton_Click(object sender, RoutedEventArgs e) => TogglePinInfoPanel();
    private void PinInfoPanelMenuItem_Click(object sender, RoutedEventArgs e) => TogglePinInfoPanel();
    private void Window_LocationChanged(object sender, EventArgs e) => UpdateDragonMirror();
    private async void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowInfoPanelTemporarily();
        await RefreshAsync(true);
    }
    private void SmallerMenuItem_Click(object sender, RoutedEventArgs e) => ApplyScale(_settings.Scale - 0.1);
    private void LargerMenuItem_Click(object sender, RoutedEventArgs e) => ApplyScale(_settings.Scale + 0.1);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current.Properties["RenderPreviewPath"] is not string)
        {
            PersistPosition();
        }
        if (_settings.MinimizeOnClose && !_forceExit)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon.Visible = true;
            if (Application.Current is App { IsCodexWatcher: true } watcher)
            {
                watcher.SuppressUntilCodexRestarts();
            }
            if (!_minimizeBalloonShown)
            {
                _notifyIcon.ShowBalloonTip(1800, "傻龙插件仍在后台运行", "双击托盘图标可恢复，右键可彻底退出。", WinForms.ToolTipIcon.Info);
                _minimizeBalloonShown = true;
            }
            return;
        }

        if (Application.Current is App { IsCodexWatcher: true } && !_forceExit)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(ExitApplication);
            return;
        }

        _activityTimer.Stop();
        _bubbleTimer.Stop();
        _countdownTimer.Stop();
        _infoPanelTimer.Stop();
        _refreshTimer.Stop();
        _pressAudio.Close();
        _releaseAudio.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    public void SavePreview(string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        encoder.Save(stream);
    }

}
