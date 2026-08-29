using System.IO;
using System.Text;
using System.Text.Json;

namespace DragonQuotaWidget;

/// <summary>
/// Reads observable Codex lifecycle events from the newest local session log.
/// It deliberately maps reasoning events to generic status text and never
/// exposes hidden reasoning payloads, user prompts, tool inputs, or secrets.
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
            if (!_tracked.TryGetValue(file.FullName, out var session))
            {
                var initialOffset = Math.Max(0, file.Length - InitialTailBytes);
                session = new TrackedSession(initialOffset, initialOffset > 0);
                _tracked[file.FullName] = session;
            }
            ReadSession(file, session);
        }

        var active = _tracked.Values
            .Where(session => session.Working)
            .OrderByDescending(session => session.EventAt)
            .FirstOrDefault();
        var nextWorking = active is not null;
        var nextStatus = active?.Status ?? "Codex 空闲中";
        var nextEventAt = active?.EventAt ?? _tracked.Values.Select(session => session.EventAt).DefaultIfEmpty(_eventAt).Max();
        if (_working != nextWorking || !string.Equals(_status, nextStatus, StringComparison.Ordinal))
        {
            _working = nextWorking;
            _status = nextStatus;
            _eventAt = nextEventAt;
            _revision++;
        }

        return Snapshot();
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
            var eventAt = TryReadTimestamp(root, out var parsed) ? parsed : DateTimeOffset.Now;

            if (outerType == "event_msg" && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                ProcessEvent(session, payload, eventAt);
            }
            else if (outerType == "response_item")
            {
                ProcessResponseItem(session, root, eventAt);
            }
        }
        catch (JsonException) { }
    }

    private void ProcessEvent(TrackedSession session, JsonElement payload, DateTimeOffset eventAt)
    {
        switch (ReadString(payload, "type"))
        {
            case "task_started":
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
                    var message = SanitizeVisibleMessage(ReadString(payload, "message"));
                    if (!string.IsNullOrWhiteSpace(message)) SetStatus(session, true, message, eventAt);
                }
                break;
            case "task_complete":
                SetStatus(session, false, "任务完成啦", eventAt, completed: true);
                break;
            case "turn_aborted":
                SetStatus(session, false, "任务已停止", eventAt, completed: true);
                break;
        }
    }

    private void ProcessResponseItem(TrackedSession session, JsonElement item, DateTimeOffset eventAt)
    {
        if (ReadString(item, "type") != "custom_tool_call") return;
        var name = ReadString(item, "name") ?? string.Empty;
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

    private void SetStatus(TrackedSession session, bool working, string status, DateTimeOffset eventAt, bool completed = false)
    {
        session.Working = working;
        session.Status = status;
        session.EventAt = eventAt;

        if (completed) _completionRevision++;
    }

    private static string? SanitizeVisibleMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var singleLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length > 44) singleLine = singleLine[..43] + "…";
        return singleLine;
    }

    private CodexActivitySnapshot Snapshot() => new(
        _working,
        _status,
        _revision,
        _completionRevision,
        _eventAt);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
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
    }
}

public sealed record CodexActivitySnapshot(
    bool IsWorking,
    string StatusText,
    long Revision,
    long CompletionRevision,
    DateTimeOffset EventAt);
