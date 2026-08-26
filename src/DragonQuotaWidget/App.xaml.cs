using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DragonQuotaWidget;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private DispatcherTimer? _codexWatchTimer;
    private MainWindow? _widgetWindow;
    private bool _codexWasRunning;
    private bool _suppressedUntilRestart;

    public bool IsCodexWatcher { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0].Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = new CodexUsageReader().ReadSnapshot();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.GetFullPath(e.Args[1]), json);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--diagnostics-root", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = new CodexUsageReader(Path.GetFullPath(e.Args[1])).ReadSnapshot();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.GetFullPath(e.Args[2]), json);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && (e.Args[0].Equals("--diagnostics-agy-root", StringComparison.OrdinalIgnoreCase) || e.Args[0].Equals("--diagnostics-agy-roots", StringComparison.OrdinalIgnoreCase)))
        {
            var roots = e.Args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
            var snapshot = new AntigravityUsageReader(customRoots: roots).ReadSnapshot();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.GetFullPath(e.Args[2]), json);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--diagnostics-agy", StringComparison.OrdinalIgnoreCase))
        {
            var roots = e.Args.Length >= 3 ? new[] { Path.GetFullPath(e.Args[1]) } : null;
            var outputPath = e.Args.Length >= 3 ? Path.GetFullPath(e.Args[2]) : Path.GetFullPath(e.Args[1]);
            var snapshot = new AntigravityUsageReader(customRoots: roots).ReadSnapshot();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--diagnostics-activity-root", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = new CodexActivityMonitor(Path.GetFullPath(e.Args[1])).Poll();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.GetFullPath(e.Args[2]), json);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            Properties["RenderPreviewPath"] = Path.GetFullPath(e.Args[1]);
            Properties["ForceInfoPanelPreview"] = true;
            if (e.Args.Length >= 3 && Enum.TryParse<WidgetMode>(e.Args[2], true, out var previewMode))
            {
                Properties["ForceWidgetMode"] = previewMode;
            }
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-preview-character-only", StringComparison.OrdinalIgnoreCase))
        {
            Properties["RenderPreviewPath"] = Path.GetFullPath(e.Args[1]);
            Properties["ForceCharacterOnlyPreview"] = true;
            Properties["ForceInteractionBubblePreview"] = true;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-preview-reset", StringComparison.OrdinalIgnoreCase))
        {
            Properties["RenderPreviewPath"] = Path.GetFullPath(e.Args[1]);
            Properties["ForceCharacterOnlyPreview"] = true;
            Properties["ForceInteractionBubblePreview"] = true;
            Properties["InteractionBubblePreviewText"] = "今日reset";
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-settings-preview", StringComparison.OrdinalIgnoreCase))
        {
            var window = new SettingsWindow(WidgetSettings.Load());
            window.Show();
            window.UpdateLayout();
            window.SavePreview(Path.GetFullPath(e.Args[1]));
            window.Close();
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, @"Local\CodexDragonQuotaWidget", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        try
        {
            var settings = WidgetSettings.Load();
            var isPreview = Properties["RenderPreviewPath"] is string;
            if (!isPreview) StartupRegistration.SetEnabled(settings.StartWithCodex);
            IsCodexWatcher = e.Args.Any(arg => arg.Equals("--watch-codex", StringComparison.OrdinalIgnoreCase));
            if (IsCodexWatcher && !settings.StartWithCodex)
            {
                Shutdown();
                return;
            }

            if (IsCodexWatcher)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _codexWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _codexWatchTimer.Tick += (_, _) => SynchronizeWithCodex();
                _codexWatchTimer.Start();
                SynchronizeWithCodex();
            }
            else
            {
                ShowWidget();
            }
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex.ToString());
            Shutdown(-1);
        }
    }

    public void SuppressUntilCodexRestarts()
    {
        if (IsCodexWatcher) _suppressedUntilRestart = true;
    }

    public void ExitCompletely()
    {
        _codexWatchTimer?.Stop();
        Shutdown();
    }

    private void SynchronizeWithCodex()
    {
        var codexRunning = CodexProcessMonitor.HasHostWindow();
        if (!codexRunning)
        {
            if (_codexWasRunning)
            {
                _suppressedUntilRestart = false;
                _widgetWindow?.SetCodexLifecycleVisible(false);
            }
        }
        else if (!_suppressedUntilRestart && (!_codexWasRunning || _widgetWindow is null || !_widgetWindow.IsVisible))
        {
            ShowWidget();
            _widgetWindow?.SetCodexLifecycleVisible(true);
        }

        _codexWasRunning = codexRunning;
    }

    private void ShowWidget()
    {
        if (_widgetWindow is null)
        {
            _widgetWindow = new MainWindow();
            MainWindow = _widgetWindow;
            _widgetWindow.Closed += (_, _) => _widgetWindow = null;
        }

        if (!_widgetWindow.IsVisible) _widgetWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _codexWatchTimer?.Stop();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void WriteErrorLog(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "CodexDragonQuotaWidget-startup.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
