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
    private readonly string _sessionsRoot;
    private string? _activePath;
    private long _offset;
    private string _partialLine = string.Empty;
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

        FileInfo? newest;
        try
        {
            newest = Directory.EnumerateFiles(_sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return Snapshot();
        }

        if (newest is null) return Snapshot();
        var switched = !string.Equals(_activePath, newest.FullName, StringComparison.OrdinalIgnoreCase);
        if (switched)
        {
            _activePath = newest.FullName;
            _offset = Math.Max(0, newest.Length - InitialTailBytes);
            _partialLine = string.Empty;
        }

        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset)
            {
                _offset = 0;
                _partialLine = string.Empty;
            }

            var available = stream.Length - _offset;
            if (available <= 0) return Snapshot();
            if (available > MaxReadBytes)
            {
                _offset = stream.Length - MaxReadBytes;
                _partialLine = string.Empty;
                switched = true;
                available = MaxReadBytes;
            }

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[(int)available];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            _offset += read;

            var text = _partialLine + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');
            _partialLine = lines[^1];
            var start = switched && _offset > read ? 1 : 0;
            for (var index = start; index < lines.Length - 1; index++)
            {
                ProcessLine(lines[index].TrimEnd('\r'));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return Snapshot();
    }

    private void ProcessLine(string line)
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
                ProcessEvent(payload, eventAt);
            }
            else if (outerType == "response_item")
            {
                ProcessResponseItem(root, eventAt);
            }
        }
        catch (JsonException) { }
    }

    private void ProcessEvent(JsonElement payload, DateTimeOffset eventAt)
    {
        switch (ReadString(payload, "type"))
        {
            case "task_started":
                SetStatus(true, "开始处理任务…", eventAt);
                break;
            case "agent_reasoning":
                SetStatus(true, "正在思考…", eventAt);
                break;
            case "patch_apply_end":
                SetStatus(true, "正在修改文件…", eventAt);
                break;
            case "web_search_end":
                SetStatus(true, "正在查找资料…", eventAt);
                break;
            case "agent_message":
                if (ReadString(payload, "phase") == "commentary")
                {
                    var message = SanitizeVisibleMessage(ReadString(payload, "message"));
                    if (!string.IsNullOrWhiteSpace(message)) SetStatus(true, message, eventAt);
                }
                break;
            case "task_complete":
                SetStatus(false, "任务完成啦", eventAt, completed: true);
                break;
            case "turn_aborted":
                SetStatus(false, "任务已停止", eventAt, completed: true);
                break;
        }
    }

    private void ProcessResponseItem(JsonElement item, DateTimeOffset eventAt)
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
        SetStatus(true, status, eventAt);
    }

    private void SetStatus(bool working, string status, DateTimeOffset eventAt, bool completed = false)
    {
        if (_working != working || !string.Equals(_status, status, StringComparison.Ordinal))
        {
            _working = working;
            _status = status;
            _eventAt = eventAt;
            _revision++;
        }

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
}

public sealed record CodexActivitySnapshot(
    bool IsWorking,
    string StatusText,
    long Revision,
    long CompletionRevision,
    DateTimeOffset EventAt);
