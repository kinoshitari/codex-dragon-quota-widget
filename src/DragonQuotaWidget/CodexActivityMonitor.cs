using System.IO;
using System.Text;
using System.Text.Json;

namespace DragonQuotaWidget;

/// <summary>
/// Reads observable Codex lifecycle events from the newest local session log.
/// Shows provider-supplied reasoning summaries and observable progress only.
/// Raw reasoning, encrypted content, user prompts and tool inputs are not displayed.
/// </summary>
public sealed class CodexActivityMonitor
{
    private const int InitialTailBytes = 512 * 1024;
    private const int MaxReadBytes = 2 * 1024 * 1024;
    private const int MaxTrackedFiles = 32;
    private readonly string _sessionsRoot;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, TrackedSession> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private bool _working;
    private string _status = "Codex 空闲中";
    private long _revision;
    private long _completionRevision;
    private DateTimeOffset _eventAt = DateTimeOffset.MinValue;
    private string? _model;
    private string? _effort;
    private CodexTurnUsage? _turnUsage;
    private string? _workDetail;
    private string? _completionText;
    private DateTimeOffset? _completedAt;

    public CodexActivityMonitor(string? codexRootOverride = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        var codexRoot = codexRootOverride;
        if (string.IsNullOrWhiteSpace(codexRoot))
        {
            codexRoot = Environment.GetEnvironmentVariable("CODEX_HOME");
        }

        if (string.IsNullOrWhiteSpace(codexRoot))
        {
            codexRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        _sessionsRoot = Path.Combine(codexRoot, "sessions");
    }

    public CodexActivitySnapshot Poll()
    {
        if (!Directory.Exists(_sessionsRoot)) return Snapshot();

        FileInfo[] newest;
        try
        {
            newest = Directory.EnumerateFiles(_sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxTrackedFiles)
                .ToArray();
        }
        catch
        {
            return Snapshot();
        }

        if (newest.Length == 0) return Snapshot();
        var selectedPaths = newest.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stalePath in _tracked.Keys.Where(path => !selectedPaths.Contains(path)).ToArray())
        {
            if (!File.Exists(stalePath))
            {
                _tracked.Remove(stalePath);
            }
            else
            {
                // Keep the cursor so a session that becomes active again does
                // not replay historical completion events. It is excluded
                // from the aggregate while outside the recent working set.
                _tracked[stalePath].Working = false;
            }
        }

        foreach (var file in newest)
        {
            if (!_tracked.TryGetValue(file.FullName, out var session) || file.Length < session.Offset)
            {
                var initialOffset = Math.Max(0, file.Length - InitialTailBytes);
                session = new TrackedSession(initialOffset, initialOffset > 0);
                if (initialOffset > 0) ReadEarlierContext(file, session, initialOffset);
                _tracked[file.FullName] = session;
            }
            ReadSession(file, session);
        }

        var active = _tracked.Values
            .Where(session => session.Working)
            .OrderByDescending(session => session.EventAt)
            .FirstOrDefault();
        var selected = active ?? _tracked.Values.OrderByDescending(session => session.EventAt).FirstOrDefault();
        var selectedUsage = selected?.Metrics.Snapshot(_clock());
        var nextWorking = active is not null;
        var nextStatus = active?.Summary ?? active?.Status ?? "Codex 空闲中";
        var nextEventAt = active?.EventAt ?? _tracked.Values.Select(session => session.EventAt).DefaultIfEmpty(_eventAt).Max();
        if (_working != nextWorking || !string.Equals(_status, nextStatus, StringComparison.Ordinal) ||
            _model != selected?.Model || _effort != selected?.Effort || _turnUsage != selectedUsage ||
            _workDetail != active?.WorkDetail)
        {
            _working = nextWorking;
            _status = nextStatus;
            _eventAt = nextEventAt;
            _model = selected?.Model;
            _effort = selected?.Effort;
            _turnUsage = selectedUsage;
            _workDetail = active?.WorkDetail;
            _revision++;
        }

        return Snapshot();
    }

    private void ReadEarlierContext(FileInfo file, TrackedSession session, long endOffset)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            long position = 0;
            while (reader.ReadLine() is { } line && position < endOffset)
            {
                position += Encoding.UTF8.GetByteCount(line) + 1;
                if (line.Contains("\"turn_context\"", StringComparison.Ordinal) ||
                    line.Contains("\"task_started\"", StringComparison.Ordinal) ||
                    line.Contains("\"turn_started\"", StringComparison.Ordinal) ||
                    line.Contains("\"task_complete\"", StringComparison.Ordinal) ||
                    line.Contains("\"turn_complete", StringComparison.Ordinal) ||
                    line.Contains("\"turn_aborted\"", StringComparison.Ordinal) ||
                    line.Contains("\"task_aborted\"", StringComparison.Ordinal) ||
                    line.Contains("\"task_failed\"", StringComparison.Ordinal) ||
                    line.Contains("\"token_count\"", StringComparison.Ordinal)) ProcessLine(session, line);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ReadSession(FileInfo file, TrackedSession session)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < session.Offset)
            {
                session.Offset = 0;
                session.PartialLine = string.Empty;
                session.SkipFirstLine = false;
            }

            var available = stream.Length - session.Offset;
            if (available <= 0) return;
            if (available > MaxReadBytes)
            {
                // A skipped turn boundary cannot be measured across the gap.
                session.Metrics.Clear();
                session.TurnId = null;
                session.Offset = stream.Length - MaxReadBytes;
                session.PartialLine = string.Empty;
                session.SkipFirstLine = session.Offset > 0;
                available = MaxReadBytes;
            }

            stream.Seek(session.Offset, SeekOrigin.Begin);
            var buffer = new byte[(int)available];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            session.Offset += read;

            var text = session.PartialLine + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');
            session.PartialLine = lines[^1];
            var start = session.SkipFirstLine ? 1 : 0;
            session.SkipFirstLine = false;
            for (var index = start; index < lines.Length - 1; index++)
            {
                ProcessLine(session, lines[index].TrimEnd('\r'));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

    }

    private void ProcessLine(TrackedSession session, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var outerType = ReadString(root, "type");
            var hasTimestamp = TryReadTimestamp(root, out var parsed);
            var eventAt = hasTimestamp ? parsed : DateTimeOffset.Now;

            if (outerType == "event_msg" && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                if (ReadString(payload, "type") == "token_count") ReadTokenMetrics(session, payload, hasTimestamp ? eventAt : null);
                else ProcessEvent(session, payload, eventAt);
            }
            else if (outerType == "turn_context" && root.TryGetProperty("payload", out var context))
            {
                var model = ReadString(context, "model");
                var effort = ReadString(context, "effort");
                var turnId = ReadString(context, "turn_id");
                if (session.Model != model || session.Effort != effort ||
                    (!string.IsNullOrEmpty(turnId) && session.TurnId != turnId)) session.Metrics.Clear();
                session.Model = model;
                session.Effort = effort;
                session.TurnId = string.IsNullOrEmpty(turnId) ? session.TurnId : turnId;
            }
            else if (outerType == "response_item" && root.TryGetProperty("payload", out var item))
            {
                ProcessResponseItem(session, item, eventAt);
            }
        }
        catch (JsonException) { }
    }

    private void ProcessEvent(TrackedSession session, JsonElement payload, DateTimeOffset eventAt)
    {
        switch (ReadString(payload, "type"))
        {
            case "task_started":
            case "turn_started":
                session.TurnId = ReadString(payload, "turn_id");
                session.Metrics.Clear();
                session.Summary = null;
                session.FinalText = null;
                session.WorkDetail = "正在准备任务";
                session.Tools.Clear();
                SetStatus(session, true, "开始处理任务…", eventAt);
                break;
            case "agent_reasoning":
                SetStatus(session, true, "正在思考…", eventAt);
                break;
            case "patch_apply_end":
                SetStatus(session, true, "正在修改文件…", eventAt);
                break;
            case "web_search_end":
                SetStatus(session, true, "正在查找资料…", eventAt);
                break;
            case "agent_message":
                if (ReadString(payload, "phase") == "commentary")
                {
                    var message = ActivityPresentation.Chinese(ReadString(payload, "message"));
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        session.Summary = message;
                        SetStatus(session, true, message, eventAt);
                    }
                }
                else if (ReadString(payload, "phase") == "final")
                    session.FinalText = ActivityPresentation.Chinese(ReadString(payload, "message"), 160);
                break;
            case "task_complete":
            case "turn_complete":
            case "turn_completed":
                session.TurnId ??= ReadString(payload, "turn_id");
                session.FinalText = ActivityPresentation.Chinese(ReadString(payload, "last_agent_message"), 160) ?? session.FinalText;
                SetStatus(session, false, "任务完成啦", eventAt, completed: true);
                break;
            case "turn_aborted":
            case "task_aborted":
            case "task_failed":
                session.TurnId ??= ReadString(payload, "turn_id");
                SetStatus(session, false, "任务已停止", eventAt, completed: true);
                break;
        }
    }

    private void ProcessResponseItem(TrackedSession session, JsonElement item, DateTimeOffset eventAt)
    {
        var type = ReadString(item, "type");
        if (type == "reasoning" && item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
        {
            var text = ActivityPresentation.Chinese(string.Join(" ", summary.EnumerateArray()
                .Where(part => ReadString(part, "type") == "summary_text")
                .Select(part => ReadString(part, "text"))));
            if (text is not null)
            {
                session.Summary = text;
                SetStatus(session, true, text, eventAt);
            }
            return;
        }
        if (type == "message" && ReadString(item, "role") == "assistant" &&
            item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join(" ", content.EnumerateArray().Where(x => ReadString(x, "type") is "output_text" or "text").Select(x => ReadString(x, "text")));
            if (ReadString(item, "phase") == "final") session.FinalText = ActivityPresentation.Chinese(text, 160);
            else if (ReadString(item, "phase") == "commentary" && ActivityPresentation.Chinese(text) is { } progress)
            {
                session.Summary = progress;
                SetStatus(session, true, progress, eventAt);
            }
            return;
        }
        if (type is "custom_tool_call_output" or "function_call_output")
        {
            var callId = ReadString(item, "call_id");
            if (callId is not null && session.Tools.Remove(callId, out var tool))
            {
                session.WorkDetail = ActivityPresentation.Tool(tool, completed: true);
                session.EventAt = eventAt;
            }
            return;
        }
        if (type is not ("custom_tool_call" or "function_call")) return;
        var name = ReadString(item, "name") ?? string.Empty;
        if (ReadString(item, "call_id") is { } id) session.Tools[id] = name;
        session.WorkDetail = ActivityPresentation.Tool(name);
        var status = name switch
        {
            var value when value.Contains("apply_patch", StringComparison.OrdinalIgnoreCase) => "正在修改文件…",
            var value when value.Contains("exec", StringComparison.OrdinalIgnoreCase) => "正在运行命令…",
            var value when value.Contains("web", StringComparison.OrdinalIgnoreCase) => "正在查找资料…",
            var value when value.Contains("view_image", StringComparison.OrdinalIgnoreCase) => "正在查看图片…",
            _ => "正在使用工具…"
        };
        SetStatus(session, true, status, eventAt);
    }

    private static bool IsCurrentTurn(TrackedSession session, JsonElement payload) =>
        ReadString(payload, "turn_id") is not { } turn || session.TurnId is null || turn == session.TurnId;

    private static void ReadTokenMetrics(TrackedSession session, JsonElement payload, DateTimeOffset? at)
    {
        if (!IsCurrentTurn(session, payload) || !payload.TryGetProperty("info", out var info)) return;
        session.Metrics.Observe(session.TurnId, at, info);
        if (at is { } eventAt) session.EventAt = eventAt;
    }

    private void SetStatus(TrackedSession session, bool working, string status, DateTimeOffset eventAt, bool completed = false)
    {
        session.Working = working;
        session.Status = status;
        session.EventAt = eventAt;

        if (completed)
        {
            session.Metrics.ConfirmTurn(session.TurnId);
            session.TurnId = null;
            _completionRevision++;
            if (_completedAt is null || eventAt >= _completedAt)
            {
                _completionText = session.FinalText ?? (status == "任务已停止" ? status : "任务已完成");
                _completedAt = eventAt;
            }
        }
    }

    private CodexActivitySnapshot Snapshot() => new(
        _working,
        _status,
        _revision,
        _completionRevision,
        _eventAt) { Model = _model, Effort = _effort, TokensPerSecond = _turnUsage?.TokensPerSecond, TurnUsage = _turnUsage,
            WorkDetail = _workDetail, CompletionText = _completionText, CompletedAt = _completedAt };

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(value.GetString(), out timestamp);
    }

    private sealed class TrackedSession(long offset, bool skipFirstLine)
    {
        public long Offset { get; set; } = offset;
        public bool SkipFirstLine { get; set; } = skipFirstLine;
        public string PartialLine { get; set; } = string.Empty;
        public bool Working { get; set; }
        public string Status { get; set; } = "Codex 空闲中";
        public DateTimeOffset EventAt { get; set; } = DateTimeOffset.MinValue;
        public string? Summary { get; set; }
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? TurnId { get; set; }
        public CodexTurnMetrics Metrics { get; } = new();
        public string? WorkDetail { get; set; }
        public string? FinalText { get; set; }
        public Dictionary<string, string> Tools { get; } = new();

    }
}

public sealed record CodexActivitySnapshot(
    bool IsWorking,
    string StatusText,
    long Revision,
    long CompletionRevision,
    DateTimeOffset EventAt)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public double? TokensPerSecond { get; init; }
    public CodexTurnUsage? TurnUsage { get; init; }
    public double? CacheHitRate => TurnUsage?.CacheHitRate;
    public bool IsSpeedStale => TurnUsage?.IsSpeedStale == true;
    public string SpeedText => TokensPerSecond is { } rate ? $"{Math.Round(rate, MidpointRounding.AwayFromZero):0} t/s" : "-- t/s";
    public string CacheHitText => CacheHitRate is { } ratio ? $"本轮缓存命中 {ratio * 100:0.0}%" : "本轮缓存命中 --";
    public string? WorkDetail { get; init; }
    public string? CompletionText { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string ModelSpeedText
    {
        get
        {
            var model = Model ?? "模型未知";
            if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) model = model[4..];
            if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) model = model[7..].Replace('-', ' ');
            else model = model.Replace("-", "");
            var effort = string.IsNullOrWhiteSpace(Effort) ? "" : $" {Effort}";
            return $"{model}{effort} · {SpeedText}";
        }
    }
}
