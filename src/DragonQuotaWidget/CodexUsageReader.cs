using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DragonQuotaWidget;

public sealed class CodexUsageReader
{
    private readonly string _sessionsRoot;
    private static readonly Regex IdRegex = new("\\\"id\\\":\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SessionIdRegex = new("\\\"session_id\\\":\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimestampRegex = new("\\\"timestamp\\\":\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OriginatorRegex = new("\\\"originator\\\":\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ThreadSourceRegex = new("\\\"thread_source\\\":\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParentThreadRegex = new("\\\"parent_thread_id\\\":\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public CodexUsageReader(string? codexRootOverride = null)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("CODEX_HOME");
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var codexRoot = !string.IsNullOrWhiteSpace(codexRootOverride)
            ? codexRootOverride
            : string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(userProfile, ".codex")
            : configuredRoot;
        _sessionsRoot = Path.Combine(codexRoot, "sessions");
    }

    public UsageSnapshot ReadSnapshot()
    {
        var now = DateTimeOffset.Now;
        var today = DateTime.Today;
        var todayStart = new DateTimeOffset(today, TimeZoneInfo.Local.GetUtcOffset(today));
        var rollingStart = now.AddHours(-24);
        var last7DaysStart = now.AddDays(-7);
        var last30DaysStart = now.AddDays(-30);
        var earliestPeriodStart = new[] { todayStart, rollingStart, last7DaysStart, last30DaysStart }.Min();
        var warnings = new HashSet<string>();

        if (!Directory.Exists(_sessionsRoot))
        {
            return UsageSnapshot.Empty(now, "未找到 .codex/sessions");
        }

        FileInfo[] files;
        try
        {
            files = Directory.EnumerateFiles(_sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Empty(now, $"会话目录不可读：{ex.Message}");
        }

        var sessions = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var info = ReadSessionInfo(file);
                if (info is not null) sessions[info.Id] = info;
            }
            catch (IOException) { warnings.Add("部分会话文件正被占用"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分会话文件无读取权限"); }
        }

        var currentRoot = sessions.Values
            .Where(session => session.IsTopLevelUser)
            .OrderByDescending(session => session.File.LastWriteTimeUtc)
            .ThenByDescending(session => session.StartedAt)
            .FirstOrDefault();

        var currentFamily = currentRoot is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : sessions.Values
                .Where(session => BelongsToRoot(session, currentRoot.Id, sessions))
                .Select(session => session.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recentForQuota = files.Take(8).Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSessions = sessions.Values.Where(session =>
            session.File.LastWriteTimeUtc >= earliestPeriodStart.UtcDateTime ||
            currentFamily.Contains(session.Id) ||
            recentForQuota.Contains(session.File.FullName)).ToArray();
        var selectedSessionIds = selectedSessions.Select(session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var todayUsage = new MutableUsageBySurface();
        var rolling = new MutableUsageBySurface();
        var last7Days = new MutableUsageBySurface();
        var last30Days = new MutableUsageBySurface();
        var allTime = new MutableUsageBySurface();
        var conversation = new MutableUsageTotals();
        RateLimitSnapshot? latestLimits = null;

        foreach (var session in selectedSessions)
        {
            try
            {
                var surface = ResolveSurface(session, sessions);
                UsageTotals? latestSessionTotal = null;
                var sessionFallbackTotal = new MutableUsageTotals();
                using var stream = new FileStream(session.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!TryReadTokenEvent(root, out var eventTime, out var payload)) continue;

                        if (payload.TryGetProperty("info", out var info) &&
                            info.ValueKind == JsonValueKind.Object &&
                            info.TryGetProperty("last_token_usage", out var lastUsage) &&
                            lastUsage.ValueKind == JsonValueKind.Object)
                        {
                            var usage = ReadUsage(lastUsage);
                            sessionFallbackTotal.Add(usage);
                            if (eventTime.ToLocalTime().Date == now.Date) todayUsage.Add(surface, usage);
                            if (eventTime >= rollingStart && eventTime <= now.AddMinutes(5)) rolling.Add(surface, usage);
                            if (eventTime >= last7DaysStart && eventTime <= now.AddMinutes(5)) last7Days.Add(surface, usage);
                            if (eventTime >= last30DaysStart && eventTime <= now.AddMinutes(5)) last30Days.Add(surface, usage);
                            if (currentFamily.Contains(session.Id)) conversation.Add(usage);

                            if (info.TryGetProperty("total_token_usage", out var totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
                            {
                                latestSessionTotal = ReadUsage(totalUsage);
                            }
                        }

                        if (payload.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
                        {
                            var parsed = ParseRateLimits(rateLimits, eventTime);
                            if (parsed is not null && (latestLimits is null || parsed.EventAt > latestLimits.EventAt)) latestLimits = parsed;
                        }
                    }
                    catch (JsonException)
                    {
                        // A live JSONL file can temporarily end with a partial line.
                    }
                }
                allTime.Add(surface, latestSessionTotal ?? sessionFallbackTotal.ToImmutable());
            }
            catch (IOException) { warnings.Add("部分会话文件正被占用"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分会话文件无读取权限"); }
        }

        foreach (var session in sessions.Values.Where(session => !selectedSessionIds.Contains(session.Id)))
        {
            try
            {
                allTime.Add(ResolveSurface(session, sessions), ReadLatestSessionTotal(session.File));
            }
            catch (IOException) { warnings.Add("部分历史会话文件正被占用"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分历史会话文件无读取权限"); }
        }

        ConversationUsage? currentConversation = null;
        if (currentRoot is not null)
        {
            currentConversation = new ConversationUsage(
                currentRoot.Id,
                ResolveSurface(currentRoot, sessions),
                conversation.ToImmutable(),
                currentRoot.StartedAt);
        }

        return new UsageSnapshot(
            todayUsage.ToImmutable(),
            rolling.ToImmutable(),
            last7Days.ToImmutable(),
            last30Days.ToImmutable(),
            allTime.ToImmutable(),
            currentConversation,
            latestLimits,
            now,
            warnings.FirstOrDefault());
    }

    private static UsageTotals ReadLatestSessionTotal(FileInfo file)
    {
        const int tailBytes = 2 * 1024 * 1024;
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - tailBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        if (offset > 0) reader.ReadLine();

        UsageTotals? latest = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!TryReadTokenEvent(document.RootElement, out _, out var payload) ||
                    !payload.TryGetProperty("info", out var info) ||
                    info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("total_token_usage", out var totalUsage) ||
                    totalUsage.ValueKind != JsonValueKind.Object) continue;
                latest = ReadUsage(totalUsage);
            }
            catch (JsonException) { }
        }

        if (latest is not null) return latest;

        stream.Seek(0, SeekOrigin.Begin);
        reader.DiscardBufferedData();
        var fallback = new MutableUsageTotals();
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!TryReadTokenEvent(document.RootElement, out _, out var payload) ||
                    !payload.TryGetProperty("info", out var info) ||
                    info.ValueKind != JsonValueKind.Object) continue;
                if (info.TryGetProperty("total_token_usage", out var totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
                {
                    latest = ReadUsage(totalUsage);
                }
                else if (info.TryGetProperty("last_token_usage", out var lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
                {
                    fallback.Add(ReadUsage(lastUsage));
                }
            }
            catch (JsonException) { }
        }

        return latest ?? fallback.ToImmutable();
    }

    private static SessionInfo? ReadSessionInfo(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return null;

        if (!line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal)) return null;
        var id = MatchValue(IdRegex, line) ?? MatchValue(SessionIdRegex, line);
        if (string.IsNullOrWhiteSpace(id)) return null;

        var originator = MatchValue(OriginatorRegex, line) ?? string.Empty;
        var threadSource = MatchValue(ThreadSourceRegex, line) ?? string.Empty;
        var parentId = MatchValue(ParentThreadRegex, line);
        var timestampText = MatchValue(TimestampRegex, line);
        var startedAt = DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTimestamp)
            ? parsedTimestamp
            : new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);

        return new SessionInfo(id, file, originator, threadSource, parentId, startedAt);
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool BelongsToRoot(SessionInfo session, string rootId, IReadOnlyDictionary<string, SessionInfo> sessions)
    {
        var current = session;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(current.ParentId) || !sessions.TryGetValue(current.ParentId, out current!)) return false;
        }

        return false;
    }

    private static UsageSurface ResolveSurface(SessionInfo session, IReadOnlyDictionary<string, SessionInfo> sessions)
    {
        var current = session;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Originator.Contains("work", StringComparison.OrdinalIgnoreCase)) return UsageSurface.Work;
            if (string.IsNullOrWhiteSpace(current.ParentId) || !sessions.TryGetValue(current.ParentId, out current!)) break;
        }

        return UsageSurface.Codex;
    }

    private static bool TryReadTokenEvent(JsonElement root, out DateTimeOffset eventTime, out JsonElement payload)
    {
        payload = default;
        return TryReadTimestamp(root, out eventTime) &&
               root.TryGetProperty("type", out var outerType) && outerType.GetString() == "event_msg" &&
               root.TryGetProperty("payload", out payload) &&
               payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "token_count";
    }

    private static UsageTotals ReadUsage(JsonElement element) => new(
        ReadInt64(element, "input_tokens"),
        ReadInt64(element, "output_tokens"),
        ReadInt64(element, "cached_input_tokens"),
        ReadInt64(element, "reasoning_output_tokens"));

    private static RateLimitSnapshot? ParseRateLimits(JsonElement element, DateTimeOffset eventTime)
    {
        var primary = TryReadWindow(element, "primary");
        var secondary = TryReadWindow(element, "secondary");
        CreditSnapshot? credits = null;
        if (element.TryGetProperty("credits", out var creditElement) && creditElement.ValueKind == JsonValueKind.Object)
        {
            credits = new CreditSnapshot(ReadBoolean(creditElement, "has_credits"), ReadBoolean(creditElement, "unlimited"), ReadString(creditElement, "balance"));
        }
        return primary is null && secondary is null && credits is null ? null : new RateLimitSnapshot(primary, secondary, credits, eventTime);
    }

    private static RateWindow? TryReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object) return null;
        var used = ReadDouble(element, "used_percent");
        var minutes = ReadNullableInt32(element, "window_minutes");
        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetElement) && resetElement.TryGetInt64(out var seconds))
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { resetsAt = null; }
        }
        return new RateWindow(Math.Clamp(used, 0d, 100d), minutes, resetsAt);
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) &&
               DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    }

    private static long ReadInt64(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
    private static double ReadDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0d;
    private static int? ReadNullableInt32(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static bool ReadBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record SessionInfo(string Id, FileInfo File, string Originator, string ThreadSource, string? ParentId, DateTimeOffset StartedAt)
    {
        public bool IsTopLevelUser => ThreadSource.Equals("user", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ParentId);
    }

    private sealed class MutableUsageTotals
    {
        public long Input { get; private set; }
        public long Output { get; private set; }
        public long Cached { get; private set; }
        public long Reasoning { get; private set; }

        public void Add(UsageTotals usage)
        {
            Input += usage.InputTokens;
            Output += usage.OutputTokens;
            Cached += usage.CachedInputTokens;
            Reasoning += usage.ReasoningOutputTokens;
        }

        public UsageTotals ToImmutable() => new(Input, Output, Cached, Reasoning);
    }

    private sealed class MutableUsageBySurface
    {
        private readonly MutableUsageTotals _codex = new();
        private readonly MutableUsageTotals _work = new();

        public void Add(UsageSurface surface, UsageTotals usage) => (surface == UsageSurface.Work ? _work : _codex).Add(usage);
        public UsageBySurface ToImmutable() => new(_codex.ToImmutable(), _work.ToImmutable());
    }
}
