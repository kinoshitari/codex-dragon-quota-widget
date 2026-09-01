using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DragonQuotaWidget;

public sealed partial class DoubaoUsageReader
{
    private const int CurrentPeriodMinutes = 300;
    private const int SevenDaysMinutes = 7 * 24 * 60;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private readonly object _cacheGate = new();
    private UsageSnapshot? _cachedSnapshot;

    public UsageSnapshot ReadSnapshot(bool forceRefresh = false)
    {
        var now = DateTimeOffset.Now;
        lock (_cacheGate)
        {
            if (!forceRefresh && _cachedSnapshot is { } cached && now - cached.ReadAt <= CacheLifetime)
                return cached;
        }

        UsageSnapshot snapshot;
        try
        {
            var texts = ReadQuotaTextsFromRunningApp();
            snapshot = ParseQuotaTexts(texts, now);
        }
        catch (DoubaoReadException ex)
        {
            snapshot = UsageSnapshot.Empty(now, ex.Message);
        }
        catch
        {
            snapshot = UsageSnapshot.Empty(now, "豆包额度读取失败，客户端界面可能已更新");
        }

        if (snapshot.RateLimits is not null)
        {
            lock (_cacheGate) _cachedSnapshot = snapshot;
        }
        return snapshot;
    }

    public static UsageSnapshot ParseQuotaTexts(IEnumerable<string> sourceTexts, DateTimeOffset now)
    {
        var texts = sourceTexts
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => Regex.Replace(text.Trim(), @"\s+", " "))
            .ToList();

        var current = ParseWindow(texts, "当前时段", CurrentPeriodMinutes, now);
        var sevenDays = ParseWindow(texts, "近 7 天", SevenDaysMinutes, now);
        if (current is null && sevenDays is null)
            return UsageSnapshot.Empty(now, "未在豆包额度面板中找到用量数据");

        var entitlement = texts.FirstOrDefault(text => text.StartsWith("赠送时长", StringComparison.Ordinal));
        return new UsageSnapshot(
            UsageBySurface.Empty,
            UsageBySurface.Empty,
            UsageBySurface.Empty,
            UsageBySurface.Empty,
            UsageBySurface.Empty,
            null,
            new RateLimitSnapshot(current, sevenDays, null, now)
            {
                LastSuccessfulFetchAt = now
            },
            now,
            entitlement);
    }

    private static RateWindow? ParseWindow(IReadOnlyList<string> texts, string heading, int windowMinutes, DateTimeOffset now)
    {
        var start = -1;
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.Equals(texts[i], heading, StringComparison.Ordinal))
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0) return null;

        string? usedText = null;
        DateTimeOffset? resetsAt = null;
        for (var i = start; i < texts.Count; i++)
        {
            var text = texts[i];
            if (text is "当前时段" or "近 7 天") break;
            if (usedText is null && text.StartsWith("已用", StringComparison.Ordinal)) usedText = text;
            if (resetsAt is null && text.Contains("重置", StringComparison.Ordinal)) resetsAt = ParseReset(text, now);
        }
        if (usedText is null) return null;

        var match = UsedPercentRegex().Match(usedText);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var used))
            return null;

        var isLessThan = match.Groups["less"].Success;
        var numericUsed = isLessThan ? Math.Max(0d, used / 2d) : used;
        var display = $"{(isLessThan ? "<" : string.Empty)}{match.Groups["value"].Value}%";
        return new RateWindow(Math.Clamp(numericUsed, 0d, 100d), windowMinutes, resetsAt)
        {
            UsedPercentText = display
        };
    }

    private static DateTimeOffset? ParseReset(string text, DateTimeOffset now)
    {
        var relative = RelativeResetRegex().Match(text);
        if (relative.Success)
        {
            var hours = relative.Groups["hours"].Success ? int.Parse(relative.Groups["hours"].Value, CultureInfo.InvariantCulture) : 0;
            var minutes = relative.Groups["minutes"].Success ? int.Parse(relative.Groups["minutes"].Value, CultureInfo.InvariantCulture) : 0;
            return now.AddHours(hours).AddMinutes(minutes);
        }

        var absolute = AbsoluteResetRegex().Match(text);
        if (!absolute.Success) return null;
        var month = int.Parse(absolute.Groups["month"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(absolute.Groups["day"].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(absolute.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(absolute.Groups["minute"].Value, CultureInfo.InvariantCulture);
        try
        {
            var candidate = new DateTimeOffset(now.Year, month, day, hour, minute, 0, now.Offset);
            return candidate < now.AddMinutes(-1) ? candidate.AddYears(1) : candidate;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadQuotaTextsFromRunningApp()
    {
        var candidates = Process.GetProcessesByName("Doubao");
        var process = candidates.FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero);
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, process)) candidate.Dispose();
        }
        if (process is null) throw new DoubaoReadException("请先启动并登录豆包电脑版");
        using var selectedProcess = process;

        AutomationElement window;
        try
        {
            window = AutomationElement.FromHandle(selectedProcess.MainWindowHandle);
        }
        catch
        {
            throw new DoubaoReadException("无法连接豆包窗口，请确认客户端正在运行");
        }

        var dialogWasOpen = FindExact(window, "当前时段") is not null && FindExact(window, "近 7 天") is not null;
        var openedDialog = false;
        AutomationElement? expandedButton = null;
        try
        {
            if (!dialogWasOpen)
            {
                expandedButton = ExpandAccountMenu(window);
                var quotaItem = WaitForExact(window, "额度状态", TimeSpan.FromSeconds(2));
                if (quotaItem is null || !TryInvoke(quotaItem))
                    throw new DoubaoReadException("未找到豆包“额度状态”入口，请确认已登录");
                openedDialog = true;
                if (WaitForExact(window, "当前时段", TimeSpan.FromSeconds(3)) is null)
                    throw new DoubaoReadException("豆包额度面板未能打开，请稍后重试");
            }

            var result = new List<string>();
            var elements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in elements)
            {
                var name = SafeName(element);
                if (name is null) continue;
                if (name.StartsWith("赠送时长", StringComparison.Ordinal) ||
                    name is "当前时段" or "近 7 天" ||
                    name.StartsWith("已用", StringComparison.Ordinal) ||
                    name.Contains("重置", StringComparison.Ordinal))
                {
                    result.Add(name);
                }
            }
            return result;
        }
        finally
        {
            if (openedDialog)
            {
                var back = WaitForExact(window, "返回", TimeSpan.FromSeconds(1));
                if (back is not null) TryInvoke(back);
            }
            else if (!dialogWasOpen && expandedButton is not null)
            {
                TryCollapse(expandedButton);
            }
        }
    }

    private static AutomationElement ExpandAccountMenu(AutomationElement window)
    {
        var elements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var attempts = 0;
        foreach (AutomationElement element in elements)
        {
            if (attempts >= 12) break;
            AutomationElement.AutomationElementInformation current;
            try { current = element.Current; }
            catch (ElementNotAvailableException) { continue; }
            if (current.ControlType != ControlType.Button || !current.IsEnabled || current.IsOffscreen) continue;
            if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var raw)) continue;
            attempts++;
            try
            {
                var pattern = (ExpandCollapsePattern)raw;
                pattern.Expand();
                if (WaitForExact(window, "额度状态", TimeSpan.FromMilliseconds(350)) is not null) return element;
                if (pattern.Current.ExpandCollapseState is ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded)
                    pattern.Collapse();
            }
            catch (InvalidOperationException) { }
            catch (ElementNotAvailableException) { }
        }
        throw new DoubaoReadException("未找到豆包账户菜单，请确认已登录");
    }

    private static AutomationElement? WaitForExact(AutomationElement root, string name, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            var found = FindExact(root, name);
            if (found is not null) return found;
            Thread.Sleep(80);
        } while (DateTime.UtcNow < until);
        return null;
    }

    private static AutomationElement? FindExact(AutomationElement root, string name)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, name);
            return root.FindFirst(TreeScope.Descendants, condition);
        }
        catch (ElementNotAvailableException) { return null; }
    }

    private static string? SafeName(AutomationElement element)
    {
        try { return element.Current.Name?.Trim(); }
        catch (ElementNotAvailableException) { return null; }
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var raw)) return false;
            ((InvokePattern)raw).Invoke();
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (ElementNotAvailableException) { return false; }
    }

    private static void TryCollapse(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var raw))
                ((ExpandCollapsePattern)raw).Collapse();
        }
        catch (InvalidOperationException) { }
        catch (ElementNotAvailableException) { }
    }

    [GeneratedRegex(@"^已用\s*(?<less><)?\s*(?<value>\d+(?:\.\d+)?)%$", RegexOptions.CultureInvariant)]
    private static partial Regex UsedPercentRegex();

    [GeneratedRegex(@"(?:(?<hours>\d+)\s*小时)?\s*(?:(?<minutes>\d+)\s*分钟)?后重置", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeResetRegex();

    [GeneratedRegex(@"(?<month>\d{1,2})月(?<day>\d{1,2})日\s*(?<hour>\d{1,2}):(?<minute>\d{2})\s*重置", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteResetRegex();

    private sealed class DoubaoReadException(string message) : Exception(message);
}
