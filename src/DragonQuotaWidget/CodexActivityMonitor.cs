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
    private readonly Dictionary<string, TrackedSession> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private bool _working;
    private string _status = "Codex 空闲中";
    private long _revision;
    private long _completionRevision;
    private DateTimeOffset _eventAt = DateTimeOffset.MinValue;
    private string? _model;
    private string? _effort;
    private GenerationSpeedSample? _speedSample;
    private string? _workDetail;
    private string? _completionText;
    private DateTimeOffset? _completedAt;

    public CodexActivityMonitor(string? codexRootOverride = null)
    {
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
        var nextWorking = active is not null;
        var nextStatus = active?.Summary ?? active?.Status ?? "Codex 空闲中";
        var nextEventAt = active?.EventAt ?? _tracked.Values.Select(session => session.EventAt).DefaultIfEmpty(_eventAt).Max();
        if (_working != nextWorking || !string.Equals(_status, nextStatus, StringComparison.Ordinal) ||
            _model != active?.Model || _effort != active?.Effort || _speedSample != active?.SpeedSample ||
            _workDetail != active?.WorkDetail)
        {
            _working = nextWorking;
            _status = nextStatus;
            _eventAt = nextEventAt;
            _model = active?.Model;
            _effort = active?.Effort;
            _speedSample = active?.SpeedSample;
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
                    line.Contains("\"task_complete\"", StringComparison.Ordinal) ||
                    line.Contains("\"turn_aborted\"", StringComparison.Ordinal) ||
                    line.Contains("\"item_completed\"", StringComparison.Ordinal) ||
                    line.Contains("\"token_usage_record\"", StringComparison.Ordinal)) ProcessLine(session, line);
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
                // A skipped response boundary cannot be timed reliably.
                session.ResetGeneration();
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
                ProcessEvent(session, payload, eventAt);
            }
            else if (outerType == "turn_context" && root.TryGetProperty("payload", out var context))
            {
                var model = ReadString(context, "model");
                var effort = ReadString(context, "effort");
                if (session.Model != model || session.Effort != effort) session.ResetGeneration();
                session.Model = model;
                session.Effort = effort;
                session.TurnId = ReadString(context, "turn_id") ?? session.TurnId;
            }
            else if (outerType == "token_usage_record" && root.TryGetProperty("payload", out var usage))
            {
                ReadResponseSpeed(session, usage, hasTimestamp ? eventAt : null);
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
                session.StartedAt = eventAt;
                session.TurnId = ReadString(payload, "turn_id");
                session.ResetGeneration();
                session.SeenResponses.Clear();
                session.SeenItems.Clear();
                session.Summary = null;
                session.FinalText = null;
                session.WorkDetail = "正在准备任务";
                session.Tools.Clear();
                SetStatus(session, true, "开始处理任务…", eventAt);
                break;
            case "item_completed":
                ReadGenerationTiming(session, payload);
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
                session.FinalText = ActivityPresentation.Chinese(ReadString(payload, "last_agent_message"), 160) ?? session.FinalText;
                SetStatus(session, false, "任务完成啦", eventAt, completed: true);
                break;
            case "turn_aborted":
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

    private static void ReadGenerationTiming(TrackedSession session, JsonElement payload)
    {
        if (!session.Working || !IsCurrentTurn(session, payload) ||
            !payload.TryGetProperty("item", out var item) ||
            ReadString(item, "type") is not ("Reasoning" or "AgentMessage") ||
            ReadString(item, "id") is not { } id ||
            !TryReadMilliseconds(payload, "started_at_ms", out var start) ||
            !TryReadMilliseconds(payload, "completed_at_ms", out var end) || end <= start ||
            session.StartedAt is not { } taskStart || start < taskStart ||
            !session.SeenItems.Add(id)) return;

        // These are client-observed model output spans, not tool durations.
        // Multiple reasoning/text items can belong to the same API response.
        session.GenerationStartedAt = session.GenerationStartedAt is { } previous ? (start < previous ? start : previous) : start;
        session.GenerationCompletedAt = session.GenerationCompletedAt is { } last ? (end > last ? end : last) : end;
    }

    private static void ReadResponseSpeed(TrackedSession session, JsonElement payload, DateTimeOffset? timestamp)
    {
        if (!session.Working || payload.ValueKind != JsonValueKind.Object || !IsCurrentTurn(session, payload)) return;
        var responseId = ReadString(payload, "response_id");
        if (responseId is not null && !session.SeenResponses.Add(responseId)) return;

        var start = session.GenerationStartedAt;
        var end = session.GenerationCompletedAt;
        session.ResetGeneration();
        if (timestamp is not { } eventAt) return;
        session.EventAt = eventAt;
        if (responseId is null || start is null || end is null || eventAt < end || eventAt <= start ||
            !payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("output_tokens", out var output) ||
            output.ValueKind != JsonValueKind.Number || !output.TryGetInt64(out var tokens) || tokens <= 0) return;

        // usage is per response; turn/thread totals and token_count are not
        // streaming samples. Token records can precede tool execution whereas
        // token_count is often written only AFTER the tool has finished.
        // Include all output (reasoning + text + tool arguments) once, through
        // response completion, rather than dividing all tokens by text-only time.
        session.SpeedSample = new(responseId, tokens, start.Value, eventAt);
    }

    private static bool TryReadMilliseconds(JsonElement element, string name, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var milliseconds)) return false;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private void SetStatus(TrackedSession session, bool working, string status, DateTimeOffset eventAt, bool completed = false)
    {
        session.Working = working;
        session.Status = status;
        session.EventAt = eventAt;

        if (completed)
        {
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
        _eventAt) { Model = _model, Effort = _effort, TokensPerSecond = _speedSample?.TokensPerSecond, SpeedSample = _speedSample,
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
        public DateTimeOffset? StartedAt { get; set; }
        public string? TurnId { get; set; }
        public DateTimeOffset? GenerationStartedAt { get; set; }
        public DateTimeOffset? GenerationCompletedAt { get; set; }
        public GenerationSpeedSample? SpeedSample { get; set; }
        public HashSet<string> SeenResponses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenItems { get; } = new(StringComparer.Ordinal);
        public string? WorkDetail { get; set; }
        public string? FinalText { get; set; }
        public Dictionary<string, string> Tools { get; } = new();

        public void ResetGeneration()
        {
            GenerationStartedAt = null;
            GenerationCompletedAt = null;
            SpeedSample = null;
        }
    }
}

public sealed record GenerationSpeedSample(string ResponseId, long OutputTokens, DateTimeOffset StartedAt, DateTimeOffset CompletedAt)
{
    public double TokensPerSecond => OutputTokens / (CompletedAt - StartedAt).TotalSeconds;
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
    public GenerationSpeedSample? SpeedSample { get; init; }
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
            var speed = TokensPerSecond is { } rate ? $"近 {rate:0.#} t/s" : "-- t/s";
            return $"{model}{effort} · {speed}";
        }
    }
}
