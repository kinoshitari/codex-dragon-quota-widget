using System.IO;
using System.Text.Json;

namespace DragonQuotaWidget;

/// <summary>Reads Claude Code transcripts and Claude Desktop's credential-free usage history.</summary>
public sealed class ClaudeUsageReader
{
    private readonly string _projectsRoot;
    private readonly string _quotaPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedSession> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CodexActivitySnapshot _activity = new(false, "Claude 空闲中", 0, 0, DateTimeOffset.MinValue);
    private DateTimeOffset? _lastCompletion;

    public ClaudeUsageReader(string? projectsRoot = null, string? quotaPath = null, Func<DateTimeOffset>? clock = null)
    {
        _projectsRoot = projectsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        _quotaPath = quotaPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public UsageSnapshot ReadSnapshot(bool forceRefresh = false)
    {
        lock (_gate)
        {
            var now = _clock();
            var sessions = ReadSessions(out var readWarning);
            var records = sessions.SelectMany(s => s.Messages.Values).ToArray();
            UsageBySurface TotalSince(DateTimeOffset from) => new(UsageTotals.Empty, UsageTotals.Empty)
            {
                Claude = records.Where(x => x.At >= from && x.At <= now).Aggregate(UsageTotals.Empty, (sum, item) => sum + item.Usage)
            };
            var current = sessions.Where(s => !s.IsSidechain).OrderByDescending(s => s.EventAt).FirstOrDefault();
            var conversation = current is null ? null : new ConversationUsage(current.Id, UsageSurface.Claude,
                current.Messages.Values.Aggregate(UsageTotals.Empty, (sum, x) => sum + x.Usage), current.StartedAt);
            RateLimitSnapshot? limits = null;
            var warning = readWarning;
            try
            {
                if (File.Exists(_quotaPath))
                {
                    using var stream = new FileStream(_quotaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    (limits, var quotaWarning) = ParseQuotaHistory(doc.RootElement, now);
                    warning ??= quotaWarning;
                }
                else warning ??= "Claude 尚未提供额度缓存，请在客户端查看用量";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                warning ??= "Claude 额度缓存暂时不可读";
            }
            var today = new DateTimeOffset(now.LocalDateTime.Date, TimeZoneInfo.Local.GetUtcOffset(now.LocalDateTime.Date));
            return new(TotalSince(today), TotalSince(now.AddHours(-24)), TotalSince(now.AddDays(-7)),
                TotalSince(now.AddDays(-30)), TotalSince(DateTimeOffset.MinValue), conversation, limits, now, warning);
        }
    }

    // Desktop 2.42304 stores utilization percentages as samples[].u.fh/sd.
    // It does not persist reset timestamps. Never infer them or missing buckets.
    public static (RateLimitSnapshot? Limits, string? Warning) ParseQuotaHistory(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 2 ||
            !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
            return (null, "Claude 用量缓存格式暂不支持");
        JsonElement? latest = null;
        long timestamp = 0;
        foreach (var item in samples.EnumerateArray())
        {
            if (item.TryGetProperty("t", out var t) && t.TryGetInt64(out var value) && value > timestamp)
            { timestamp = value; latest = item; }
        }
        if (latest is not { } sample || !sample.TryGetProperty("u", out var usage))
            return (null, "Claude 尚未提供额度数据");
        RateWindow? Window(string key, int minutes) => usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var percent) &&
            double.IsFinite(percent) && percent is >= 0 and <= 100 ? new(percent, minutes, null) : null;
        var five = Window("fh", 300);
        var weekly = Window("sd", 10080);
        if (five is null && weekly is null)
            return (null, "Claude 当前账号未提供额度百分比（Free 或第三方 API 可能不提供）");
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return (null, "Claude 用量缓存时间无效"); }
        var age = now - at;
        if (age > TimeSpan.FromMinutes(30) || age < TimeSpan.FromMinutes(-1))
            return (null, "Claude 额度缓存已过期，请在客户端刷新用量");
        var stale = age > TimeSpan.FromMinutes(5);
        return (new(five, weekly, null, at) { IsStale = stale, LastSuccessfulFetchAt = at },
            stale ? "Claude 额度为旧缓存，请在客户端刷新用量" : "Claude 客户端缓存；未提供重置时间");
    }

    public CodexActivitySnapshot Poll()
    {
        lock (_gate)
        {
            var session = ReadSessions(out _).Where(s => !s.IsSidechain).OrderByDescending(s => s.EventAt).FirstOrDefault();
            var working = session?.Working == true;
            var next = new CodexActivitySnapshot(working, working ? session!.Summary ?? "正在处理任务…" : "Claude 空闲中",
                _activity.Revision, _activity.CompletionRevision, session?.EventAt ?? DateTimeOffset.MinValue)
            {
                Model = working ? session?.Model : null, Effort = working ? session?.Effort : null,
                WorkDetail = session?.WorkDetail,
                CompletionText = session?.FinalText, CompletedAt = session?.CompletedAt
            };
            if (session?.CompletedAt is { } completed && completed != _lastCompletion)
            {
                _lastCompletion = completed;
                next = next with { CompletionRevision = _activity.CompletionRevision + 1 };
            }
            if (next != _activity) next = next with { Revision = _activity.Revision + 1 };
            return _activity = next;
        }
    }

    private List<Session> ReadSessions(out string? warning)
    {
        warning = null;
        var result = new List<Session>();
        if (!Directory.Exists(_projectsRoot)) { warning = "未找到 Claude Code 本地会话"; return result; }
        try
        {
            var files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories).Select(path => new FileInfo(path)).ToArray();
            var present = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in _cache.Keys.Where(k => !present.Contains(k)).ToArray()) _cache.Remove(old);
            foreach (var file in files)
            {
                try
                {
                    if (!_cache.TryGetValue(file.FullName, out var cached) || cached.Length != file.Length || cached.Written != file.LastWriteTimeUtc)
                        _cache[file.FullName] = cached = new(file.Length, file.LastWriteTimeUtc, ReadSession(file.FullName));
                    result.Add(cached.Value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = "部分 Claude 会话暂时不可读"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = "Claude 会话目录暂时不可读"; }
        // One session can have multiple identical copies. Keep the newest record.
        return result.GroupBy(s => s.Id).Select(g => g.OrderByDescending(s => s.EventAt).First()).ToList();
    }

    private static Session ReadSession(string path)
    {
        var session = new Session { Id = Path.GetFileNameWithoutExtension(path) };
        var tools = new Dictionary<string, string>();
        string? lastText = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var item = doc.RootElement;
                if (!DateTimeOffset.TryParse(Text(item, "timestamp"), out var at)) continue;
                session.Id = Text(item, "sessionId") ?? session.Id;
                session.IsSidechain |= item.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
                if (session.StartedAt == default) session.StartedAt = at;
                var type = Text(item, "type");
                if (type == "system" && Text(item, "subtype") == "stop_hook_summary")
                {
                    session.Working = false;
                    session.CompletedAt = at;
                    session.FinalText = ActivityPresentation.Chinese(lastText, 160) ?? "任务已完成";
                    session.EventAt = at;
                    continue;
                }
                if (!item.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) continue;
                var blocks = content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                if (type == "user")
                {
                    var results = blocks.Where(x => Text(x, "type") == "tool_result").ToArray();
                    if (results.Length > 0)
                    {
                        foreach (var block in results)
                            if (Text(block, "tool_use_id") is { } toolId && tools.TryGetValue(toolId, out var name))
                                session.WorkDetail = ActivityPresentation.Tool(name, completed: true);
                    }
                    else
                    {
                        tools.Clear(); lastText = null;
                        session.Working = true; session.Summary = null; session.FinalText = null; session.CompletedAt = null;
                        session.WorkDetail = "正在准备任务";
                    }
                    session.EventAt = at;
                }
                else if (type == "assistant")
                {
                    session.EventAt = at;
                    var model = Text(message, "model");
                    if (model is { Length: > 0 } && model != "<synthetic>") session.Model = model;
                    session.Effort = Text(item, "perTurnEffort") ?? Text(item, "effort") ?? session.Effort;
                    var messageId = Text(message, "id") ?? Text(item, "uuid");
                    if (messageId is not null && message.TryGetProperty("usage", out var usage))
                    {
                        var cached = Number(usage, "cache_read_input_tokens");
                        var tokens = new UsageTotals(Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + cached,
                            Number(usage, "output_tokens"), cached, 0);
                        if (!session.Messages.TryGetValue(messageId, out var previous) || tokens.TotalTokens >= previous.Usage.TotalTokens)
                            session.Messages[messageId] = new(at, tokens);
                        // Transcript timestamps mark saved content blocks, not
                        // first/last streamed tokens. Repeated blocks can even
                        // carry the same FINAL token count at different times.
                        // cost-state API time is session-wide and includes TTFT;
                        // neither can establish a response's generation TPS.
                    }
                    foreach (var block in blocks)
                    {
                        switch (Text(block, "type"))
                        {
                            case "text":
                                lastText = Text(block, "text");
                                session.Summary = ActivityPresentation.Chinese(lastText) ?? session.Summary;
                                break;
                            case "tool_use":
                                var name = Text(block, "name");
                                if (Text(block, "id") is { } id) tools[id] = name ?? "";
                                session.WorkDetail = ActivityPresentation.Tool(name);
                                break;
                            // 'thinking' is raw reasoning, not a public summary.
                        }
                    }
                    var failed = item.TryGetProperty("isApiErrorMessage", out var error) && error.ValueKind == JsonValueKind.True;
                    if (Text(message, "stop_reason") == "end_turn" || failed)
                    {
                        session.Working = false; session.CompletedAt = at;
                        session.FinalText = ActivityPresentation.Chinese(lastText, 160) ?? (failed ? "任务遇到错误，请查看 Claude" : "任务已完成");
                    }
                }
            }
            catch (JsonException) { }
        }
        return session;
    }

    private static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var value) ? Math.Max(0, value) : 0;
    private sealed record MessageUsage(DateTimeOffset At, UsageTotals Usage);
    private sealed record CachedSession(long Length, DateTime Written, Session Value);
    private sealed class Session
    {
        public string Id = "";
        public bool IsSidechain;
        public DateTimeOffset StartedAt;
        public DateTimeOffset EventAt;
        public bool Working;
        public string? Summary, WorkDetail, FinalText, Model, Effort;
        public DateTimeOffset? CompletedAt;
        public Dictionary<string, MessageUsage> Messages { get; } = new();
    }
}
