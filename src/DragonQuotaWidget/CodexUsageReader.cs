using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DragonQuotaWidget;

public sealed class CodexUsageReader
{
    private readonly string _sessionsRoot;
    private readonly string _archivedRoot;
    private readonly Dictionary<string, CachedSessionSummary> _historicalCache = new(StringComparer.OrdinalIgnoreCase);
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
        _archivedRoot = Path.Combine(codexRoot, "archived_sessions");
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

        if (!Directory.Exists(_sessionsRoot) && !Directory.Exists(_archivedRoot))
        {
            return UsageSnapshot.Empty(now, "未找到 .codex/sessions");
        }

        FileInfo[] files;
        try
        {
            files = new[] { _sessionsRoot, _archivedRoot }.Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
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
                if (info is not null && !sessions.TryAdd(info.Id, info))
                {
                    // A resumed thread can have several physical rollouts.
                    // Keep all fragments; duplicated token events are removed
                    // when the complete thread history is read.
                    var existing = sessions[info.Id];
                    existing.HistoryFiles.Add(file);
                    if (info.StartedAt < existing.HistoryStartedAt) existing.HistoryStartedAt = info.StartedAt;
                }
            }
            catch (IOException) { warnings.Add("部分会话文件正被占用"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分会话文件无读取权限"); }
            catch (JsonException) { warnings.Add("部分会话元数据不完整"); }
        }

        var existingPaths = files.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stalePath in _historicalCache.Keys.Where(path => !existingPaths.Contains(path)).ToArray())
        {
            _historicalCache.Remove(stalePath);
        }

        var currentRoot = sessions.Values
            .Where(session => session.IsTopLevelUser && !session.File.FullName.StartsWith(_archivedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.File.LastWriteTimeUtc)
            .ThenByDescending(session => session.StartedAt)
            .FirstOrDefault();

        var currentFamily = currentRoot is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : sessions.Values
                .Where(session => BelongsToRoot(session, currentRoot.Id, sessions))
                .Select(session => session.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectedSessions = sessions.Values.Where(session =>
            session.File.LastWriteTimeUtc >= earliestPeriodStart.UtcDateTime ||
            currentFamily.Contains(session.Id)).ToArray();
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
                var counter = new TokenCounter();
                var sessionTotalUsage = new MutableUsageTotals();
                RateLimitSnapshot? latestSessionLimits = null;
                foreach (var tokenEvent in ReadTokenEvents(session))
                {
                    var eventTime = tokenEvent.Timestamp;
                    var payload = tokenEvent.Payload;

                    if (payload.TryGetProperty("info", out var info) &&
                        info.ValueKind == JsonValueKind.Object)
                    {
                        var usage = counter.Read(info, session.IsFork);
                        // Replayed history in a fork belongs to its source session.
                        if ((!session.IsFork || eventTime >= session.HistoryStartedAt) && eventTime <= now)
                        {
                            sessionTotalUsage.Add(usage);
                            if (eventTime.ToLocalTime().Date == now.Date) todayUsage.Add(surface, usage);
                            if (eventTime >= rollingStart) rolling.Add(surface, usage);
                            if (eventTime >= last7DaysStart) last7Days.Add(surface, usage);
                            if (eventTime >= last30DaysStart) last30Days.Add(surface, usage);
                        }
                    }

                    if (payload.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseRateLimits(rateLimits, eventTime);
                        if (parsed is not null)
                        {
                            if (latestLimits is null || parsed.EventAt > latestLimits.EventAt) latestLimits = parsed;
                            if (latestSessionLimits is null || parsed.EventAt > latestSessionLimits.EventAt) latestSessionLimits = parsed;
                        }
                    }
                }
                var sessionTotal = sessionTotalUsage.ToImmutable() + counter.HistoryBaseline;
                if (currentFamily.Contains(session.Id)) conversation.Add(sessionTotal);
                allTime.Add(surface, sessionTotal);
                _historicalCache[session.File.FullName] = CachedSessionSummary.FromSession(session, sessionTotal, latestSessionLimits);
            }
            catch (IOException) { warnings.Add("部分会话文件正被占用"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分会话文件无读取权限"); }
        }

        foreach (var session in sessions.Values.Where(session => !selectedSessionIds.Contains(session.Id)))
        {
            try
            {
                var summary = ReadCachedSessionSummary(session);
                allTime.Add(ResolveSurface(session, sessions), summary.Usage);
                if (summary.RateLimits is not null && (latestLimits is null || summary.RateLimits.EventAt > latestLimits.EventAt))
                {
                    latestLimits = summary.RateLimits;
                }
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
                currentRoot.HistoryStartedAt);
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

    private CachedSessionSummary ReadCachedSessionSummary(SessionInfo session)
    {
        var file = session.File;
        if (_historicalCache.TryGetValue(file.FullName, out var cached) && cached.Matches(session))
        {
            return cached;
        }

        var parsed = ReadHistoricalSessionSummary(session);
        _historicalCache[file.FullName] = parsed;
        return parsed;
    }

    private static CachedSessionSummary ReadHistoricalSessionSummary(SessionInfo session)
    {
        var counter = new TokenCounter();
        var usage = new MutableUsageTotals();
        RateLimitSnapshot? latestLimits = null;
        foreach (var tokenEvent in ReadTokenEvents(session))
        {
            var eventTime = tokenEvent.Timestamp;
            var payload = tokenEvent.Payload;
            if (payload.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseRateLimits(rateLimits, eventTime);
                if (parsed is not null && (latestLimits is null || parsed.EventAt > latestLimits.EventAt)) latestLimits = parsed;
            }
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                var delta = counter.Read(info, session.IsFork);
                if ((!session.IsFork || eventTime >= session.HistoryStartedAt) && eventTime <= DateTimeOffset.Now) usage.Add(delta);
            }
        }
        return CachedSessionSummary.FromSession(session, usage.ToImmutable() + counter.HistoryBaseline, latestLimits);
    }

    private static IEnumerable<(DateTimeOffset Timestamp, JsonElement Payload)> ReadTokenEvents(SessionInfo session)
    {
        var events = new List<(DateTimeOffset Timestamp, JsonElement Payload)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in session.HistoryFiles)
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (!TryReadTokenEvent(document.RootElement, out var timestamp, out var payload)) continue;
                    if (seen.Add($"{timestamp.UtcTicks}:{payload.GetRawText()}")) events.Add((timestamp, payload.Clone()));
                }
                catch (JsonException) { } // A live log may end with an incomplete record.
            }
        }
        return events.OrderBy(item => item.Timestamp);
    }

    private sealed class TokenCounter
    {
        private UsageTotals? _previous;
        public UsageTotals HistoryBaseline { get; private set; } = UsageTotals.Empty;

        public UsageTotals Read(JsonElement info, bool isFork)
        {
            var hasLast = info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object;
            var delta = hasLast ? ReadUsage(last) : UsageTotals.Empty;
            if (info.TryGetProperty("total_token_usage", out var totalElement) && totalElement.ValueKind == JsonValueKind.Object)
            {
                var total = ReadUsage(totalElement);
                if (_previous is { } previous)
                {
                    // A reset starts a new counter segment; accumulated usage is retained.
                    if (total.InputTokens < previous.InputTokens || total.OutputTokens < previous.OutputTokens)
                    {
                        delta = hasLast ? ReadUsage(last) : total;
                        if (hasLast) RetainBaseline(total, delta);
                    }
                    else
                        delta = new UsageTotals(
                            total.InputTokens - previous.InputTokens,
                            total.OutputTokens - previous.OutputTokens,
                            Math.Max(0, total.CachedInputTokens - previous.CachedInputTokens),
                            Math.Max(0, total.ReasoningOutputTokens - previous.ReasoningOutputTokens));
                }
                else if (!isFork)
                {
                    if (!hasLast) delta = total;
                    else
                    {
                        // Resumed logs may begin with a cumulative history plus
                        // the latest request. Keep the earlier history in the
                        // conversation/all-time totals, outside timed windows.
                        RetainBaseline(total, delta);
                    }
                }
                // The first cumulative value may contain inherited/resumed history.
                _previous = total;
            }
            else if (_previous is not null) _previous += delta;
            return delta;
        }

        private void RetainBaseline(UsageTotals total, UsageTotals delta) =>
            HistoryBaseline += new UsageTotals(
                Math.Max(0, total.InputTokens - delta.InputTokens),
                Math.Max(0, total.OutputTokens - delta.OutputTokens),
                Math.Max(0, total.CachedInputTokens - delta.CachedInputTokens),
                Math.Max(0, total.ReasoningOutputTokens - delta.ReasoningOutputTokens));
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

        using var document = JsonDocument.Parse(line);
        var isFork = document.RootElement.TryGetProperty("payload", out var metadata) &&
            ReadString(metadata, "forked_from_id") is { Length: > 0 };
        return new SessionInfo(id, file, originator, threadSource, parentId, startedAt, isFork);
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

    private sealed record SessionInfo(string Id, FileInfo File, string Originator, string ThreadSource, string? ParentId, DateTimeOffset StartedAt, bool IsFork)
    {
        public List<FileInfo> HistoryFiles { get; } = [File];
        public DateTimeOffset HistoryStartedAt { get; set; } = StartedAt;
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

    private sealed record CachedSessionSummary(string Signature, UsageTotals Usage, RateLimitSnapshot? RateLimits)
    {
        private static string GetSignature(SessionInfo session) => string.Join(";", session.HistoryFiles.Select(file => $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        public bool Matches(SessionInfo session) => Signature == GetSignature(session);
        public static CachedSessionSummary FromSession(SessionInfo session, UsageTotals usage, RateLimitSnapshot? rateLimits) =>
            new(GetSignature(session), usage, rateLimits);
    }
}
