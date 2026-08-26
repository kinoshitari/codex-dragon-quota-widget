using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DragonQuotaWidget;

public sealed class AntigravityUsageReader
{
    internal const int MaxMetadataBlobBytes = 8 * 1024 * 1024;
    public delegate (int ExitCode, string StandardOutput, string StandardError) CommandRunner(string fileName, string arguments, TimeSpan timeout);

    private readonly IReadOnlyList<string> _roots;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CommandRunner? _quotaRunner;
    private readonly TimeSpan _quotaCacheDuration;

    private RateLimitSnapshot? _cachedRateLimits;
    private DateTimeOffset _lastQuotaFetchTime = DateTimeOffset.MinValue;
    private readonly object _quotaLock = new();

    public AntigravityUsageReader(
        IEnumerable<string>? customRoots = null,
        Func<DateTimeOffset>? clock = null,
        CommandRunner? quotaRunner = null,
        TimeSpan? quotaCacheDuration = null)
    {
        _roots = customRoots?.ToArray() ?? GetDefaultRoots();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _quotaRunner = quotaRunner;
        _quotaCacheDuration = quotaCacheDuration ?? TimeSpan.FromSeconds(30);
    }

    public static IReadOnlyList<string> GetDefaultRoots()
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return new[]
        {
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversations"),
            Path.Combine(userProfile, ".gemini", "antigravity", "conversations")
        };
    }

    public UsageSnapshot ReadSnapshot()
    {
        var now = _clock();
        var today = now.LocalDateTime.Date;
        var rollingStart = now.AddHours(-24);
        var last7DaysStart = now.AddDays(-7);
        var last30DaysStart = now.AddDays(-30);
        var warnings = new HashSet<string>();

        var discoveredFiles = DiscoverDatabases(_roots, warnings);
        var deduplicated = DeduplicateDatabases(discoveredFiles, warnings);

        var todayUsage = new MutableUsageTotals();
        var rollingUsage = new MutableUsageTotals();
        var last7DaysUsage = new MutableUsageTotals();
        var last30DaysUsage = new MutableUsageTotals();
        var allTimeUsage = new MutableUsageTotals();

        ConversationCandidate? latestTrajectory = null;

        foreach (var file in deduplicated)
        {
            try
            {
                var events = ReadTrajectoryEvents(file.FilePath, warnings);
                if (events.Count == 0) continue;

                var trajTotals = new MutableUsageTotals();
                DateTimeOffset trajEarliest = DateTimeOffset.MaxValue;
                DateTimeOffset trajLatest = DateTimeOffset.MinValue;

                foreach (var (eventTime, usage) in events)
                {
                    trajTotals.Add(usage);
                    allTimeUsage.Add(usage);

                    if (eventTime.ToLocalTime().Date == today) todayUsage.Add(usage);
                    if (eventTime >= rollingStart && eventTime <= now.AddMinutes(5)) rollingUsage.Add(usage);
                    if (eventTime >= last7DaysStart && eventTime <= now.AddMinutes(5)) last7DaysUsage.Add(usage);
                    if (eventTime >= last30DaysStart && eventTime <= now.AddMinutes(5)) last30DaysUsage.Add(usage);

                    if (eventTime < trajEarliest) trajEarliest = eventTime;
                    if (eventTime > trajLatest) trajLatest = eventTime;
                }

                if (latestTrajectory is null || trajLatest > latestTrajectory.LatestEventTime)
                {
                    latestTrajectory = new ConversationCandidate(
                        file.TrajectoryId,
                        trajTotals.ToImmutable(),
                        trajEarliest,
                        trajLatest);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"读取数据库失败 ({Path.GetFileName(file.FilePath)}): {ex.Message}");
            }
        }

        ConversationUsage? currentConversation = null;
        if (latestTrajectory is not null)
        {
            currentConversation = new ConversationUsage(
                latestTrajectory.TrajectoryId,
                UsageSurface.Agy,
                latestTrajectory.Totals,
                latestTrajectory.StartedAt);
        }

        RateLimitSnapshot? rateLimits = null;
        try
        {
            rateLimits = FetchRateLimits(now, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"获取额度失败: {ex.Message}");
        }

        var warningMessage = warnings.Count > 0 ? string.Join("; ", warnings) : null;

        return new UsageSnapshot(
            new UsageBySurface(UsageTotals.Empty, UsageTotals.Empty, todayUsage.ToImmutable()),
            new UsageBySurface(UsageTotals.Empty, UsageTotals.Empty, rollingUsage.ToImmutable()),
            new UsageBySurface(UsageTotals.Empty, UsageTotals.Empty, last7DaysUsage.ToImmutable()),
            new UsageBySurface(UsageTotals.Empty, UsageTotals.Empty, last30DaysUsage.ToImmutable()),
            new UsageBySurface(UsageTotals.Empty, UsageTotals.Empty, allTimeUsage.ToImmutable()),
            currentConversation,
            rateLimits,
            now,
            warningMessage);
    }

    private static List<FileInfo> DiscoverDatabases(IReadOnlyList<string> roots, HashSet<string> warnings)
    {
        var result = new List<FileInfo>();
        bool anyRootExisted = false;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            anyRootExisted = true;
            try
            {
                var files = Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path));
                result.AddRange(files);
            }
            catch (Exception ex)
            {
                warnings.Add($"扫描目录失败 ({root}): {ex.Message}");
            }
        }

        if (!anyRootExisted && roots.Count > 0)
        {
            warnings.Add("未找到 AGY 对话目录");
        }

        return result;
    }

    private static List<DeduplicatedDatabase> DeduplicateDatabases(List<FileInfo> files, HashSet<string> warnings)
    {
        var grouped = new Dictionary<string, DeduplicatedDatabase>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                using var db = WinSqliteDatabase.OpenReadonly(file.FullName);
                var trajectoryId = db.ReadTrajectoryId();
                if (string.IsNullOrWhiteSpace(trajectoryId))
                {
                    warnings.Add($"无法读取轨迹元数据: {file.Name}");
                    continue;
                }

                if (!grouped.TryGetValue(trajectoryId, out var existing) || file.LastWriteTimeUtc > existing.LastWriteTimeUtc)
                {
                    grouped[trajectoryId] = new DeduplicatedDatabase(trajectoryId, file.FullName, file.LastWriteTimeUtc);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"打开数据库失败 ({file.Name}): {ex.Message}");
            }
        }

        return grouped.Values.ToList();
    }

    private static List<(DateTimeOffset Timestamp, UsageTotals Usage)> ReadTrajectoryEvents(string filePath, HashSet<string> warnings)
    {
        var events = new List<(DateTimeOffset Timestamp, UsageTotals Usage)>();

        using var db = WinSqliteDatabase.OpenReadonly(filePath);
        var blobs = db.ReadStepMetadataBlobs();

        foreach (var blob in blobs)
        {
            if (TryParseStepMetadata(blob, out var eventTime, out var usage))
            {
                events.Add((eventTime, usage));
            }
        }

        return events;
    }

    public static bool TryParseStepMetadata(
        ReadOnlySpan<byte> bytes,
        out DateTimeOffset timestamp,
        out UsageTotals usage)
    {
        timestamp = default;
        usage = UsageTotals.Empty;

        var reader = new ProtobufSpanReader(bytes);
        bool hasTimestamp = false;
        bool hasGeneration = false;
        DateTimeOffset parsedTimestamp = default;
        UsageTotals parsedUsage = UsageTotals.Empty;

        while (reader.TryReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1 && wireType == 2)
            {
                if (reader.TryReadLengthDelimited(out var timestampSlice))
                {
                    if (TryParseTimestamp(timestampSlice, out parsedTimestamp))
                    {
                        hasTimestamp = true;
                    }
                }
                else
                {
                    return false;
                }
            }
            else if (fieldNumber == 9 && wireType == 2)
            {
                if (reader.TryReadLengthDelimited(out var genSlice))
                {
                    if (TryParseGenerationMetadata(genSlice, out parsedUsage))
                    {
                        hasGeneration = true;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (!reader.TrySkip(wireType))
                {
                    return false;
                }
            }
        }

        if (!hasTimestamp || !hasGeneration)
        {
            return false;
        }

        timestamp = parsedTimestamp;
        usage = parsedUsage;
        return true;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<byte> bytes, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var reader = new ProtobufSpanReader(bytes);
        long seconds = 0;
        int nanos = 0;

        while (reader.TryReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1 && wireType == 0)
            {
                if (!reader.TryReadInt64(out seconds)) return false;
            }
            else if (fieldNumber == 2 && wireType == 0)
            {
                if (!reader.TryReadInt32(out nanos)) return false;
            }
            else
            {
                if (!reader.TrySkip(wireType)) return false;
            }
        }

        try
        {
            if (seconds < 0 || seconds > 253402300799L)
            {
                return false;
            }
            nanos = Math.Clamp(nanos, 0, 999_999_999);
            timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseGenerationMetadata(ReadOnlySpan<byte> bytes, out UsageTotals totals)
    {
        totals = UsageTotals.Empty;
        var reader = new ProtobufSpanReader(bytes);

        long field2 = 0;  // non-cached input
        long field3 = 0;  // total output
        long field5 = 0;  // cached input
        long field9 = 0;  // visible answer output
        long field10 = 0; // reasoning output

        while (reader.TryReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 2 && wireType == 0)
            {
                if (!reader.TryReadInt64(out field2)) return false;
            }
            else if (fieldNumber == 3 && wireType == 0)
            {
                if (!reader.TryReadInt64(out field3)) return false;
            }
            else if (fieldNumber == 5 && wireType == 0)
            {
                if (!reader.TryReadInt64(out field5)) return false;
            }
            else if (fieldNumber == 9 && wireType == 0)
            {
                if (!reader.TryReadInt64(out field9)) return false;
            }
            else if (fieldNumber == 10 && wireType == 0)
            {
                if (!reader.TryReadInt64(out field10)) return false;
            }
            else
            {
                if (!reader.TrySkip(wireType)) return false;
            }
        }

        field2 = Math.Max(0, field2);
        field3 = Math.Max(0, field3);
        field5 = Math.Max(0, field5);
        field9 = Math.Max(0, field9);
        field10 = Math.Max(0, field10);

        long inputTokens = field2 + field5;
        long outputTokens = field3;
        long cachedInputTokens = field5;
        long reasoningOutputTokens = Math.Min(field10, outputTokens);

        totals = new UsageTotals(inputTokens, outputTokens, cachedInputTokens, reasoningOutputTokens);
        return true;
    }

    private RateLimitSnapshot? FetchRateLimits(DateTimeOffset now, HashSet<string> warnings)
    {
        lock (_quotaLock)
        {
            if (_cachedRateLimits is not null && (now - _lastQuotaFetchTime) < _quotaCacheDuration)
            {
                return _cachedRateLimits;
            }

            var snapshot = QueryQuota(now, warnings);
            if (snapshot is not null)
            {
                _cachedRateLimits = snapshot;
                _lastQuotaFetchTime = now;
            }

            return snapshot ?? _cachedRateLimits;
        }
    }

    private RateLimitSnapshot? QueryQuota(DateTimeOffset now, HashSet<string> warnings)
    {
        string exePath = ResolveAgyExecutable();

        if (_quotaRunner is not null)
        {
            try
            {
                var (exitCode, stdout, stderr) = _quotaRunner(exePath, "-p /usage --output-format json --print-timeout 30s", TimeSpan.FromSeconds(35));
                if (exitCode != 0)
                {
                    warnings.Add($"获取额度命令退出代码 {exitCode}");
                    return null;
                }
                return ParseQuotaJson(stdout, now);
            }
            catch (Exception ex)
            {
                warnings.Add($"运行额度命令失败: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(exePath))
        {
            warnings.Add("未找到 agy.exe");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-p /usage --output-format json --print-timeout 30s",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool completed = process.WaitForExit(35000);
            if (!completed)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }
                warnings.Add("获取额度超时");
                return null;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                warnings.Add($"获取额度失败 (退出码 {process.ExitCode})");
                return null;
            }

            var parsed = ParseQuotaJson(stdout, now);
            if (parsed is null)
            {
                warnings.Add("无法解析 AGY 额度响应");
            }
            return parsed;
        }
        catch (Exception ex)
        {
            warnings.Add($"执行 agy.exe 失败: {ex.Message}");
            return null;
        }
    }

    public static string ResolveAgyExecutable()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var preferredPath = Path.Combine(localAppData, "agy", "bin", "agy.exe");
            if (File.Exists(preferredPath))
            {
                return preferredPath;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            var entries = pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                try
                {
                    var candidate = Path.Combine(entry, "agy.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "agy", "bin", "agy.exe");
        }

        return "agy.exe";
    }

    public static RateLimitSnapshot? ParseQuotaJson(string stdout, DateTimeOffset eventTime)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        // 1. Try parsing trimmed stdout as a whole
        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (TryParseQuotaElement(doc.RootElement, eventTime, out var snapshot))
                {
                    return snapshot;
                }
            }
            catch (JsonException) { }
        }

        // 2. Try single-line JSON candidates from bottom to top
        var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('{') && line.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (TryParseQuotaElement(doc.RootElement, eventTime, out var snapshot))
                    {
                        return snapshot;
                    }
                }
                catch (JsonException) { }
            }
        }

        // 3. Search backwards for multi-line JSON blocks starting with '{' and ending at the last '}'
        int lastEndBrace = stdout.LastIndexOf('}');
        if (lastEndBrace >= 0)
        {
            int searchPos = 0;
            var startIndices = new List<int>();
            while (searchPos < lastEndBrace)
            {
                int idx = stdout.IndexOf('{', searchPos);
                if (idx < 0 || idx > lastEndBrace) break;
                startIndices.Add(idx);
                searchPos = idx + 1;
            }

            for (int i = startIndices.Count - 1; i >= 0; i--)
            {
                int start = startIndices[i];
                var candidate = stdout.Substring(start, lastEndBrace - start + 1).Trim();
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    if (TryParseQuotaElement(doc.RootElement, eventTime, out var snapshot))
                    {
                        return snapshot;
                    }
                }
                catch (JsonException) { }
            }
        }

        return null;
    }

    private static bool TryParseQuotaElement(JsonElement root, DateTimeOffset eventTime, out RateLimitSnapshot? snapshot)
    {
        snapshot = null;

        if (root.TryGetProperty("status", out var statusElem) &&
            statusElem.GetString() is { } status &&
            !status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!root.TryGetProperty("command", out var cmdElem) || cmdElem.ValueKind != JsonValueKind.Object)
            return false;
        if (!cmdElem.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Object)
            return false;
        if (!dataElem.TryGetProperty("groups", out var groupsElem) || groupsElem.ValueKind != JsonValueKind.Array)
            return false;

        JsonElement? geminiGroup = null;
        foreach (var group in groupsElem.EnumerateArray())
        {
            if (group.TryGetProperty("name", out var nameElem) &&
                nameElem.GetString() is { } name &&
                name.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                geminiGroup = group;
                break;
            }
        }

        if (geminiGroup is null)
        {
            return false;
        }

        if (!geminiGroup.Value.TryGetProperty("buckets", out var bucketsElem) || bucketsElem.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        RateWindow? weeklyWindow = null;
        RateWindow? fiveHourWindow = null;

        foreach (var bucket in bucketsElem.EnumerateArray())
        {
            if (!bucket.TryGetProperty("window", out var windowElem) || windowElem.GetString() is not { } windowStr)
                continue;

            double remainingFraction = 0d;
            if (bucket.TryGetProperty("remaining_fraction", out var remElem) && remElem.TryGetDouble(out var remVal))
            {
                remainingFraction = remVal;
            }

            DateTimeOffset? resetTime = null;
            if (bucket.TryGetProperty("reset_time", out var resetElem) && resetElem.GetString() is { } resetStr)
            {
                if (DateTimeOffset.TryParse(resetStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
                {
                    resetTime = parsedReset;
                }
            }

            double usedPercent = Math.Clamp((1.0 - remainingFraction) * 100.0, 0.0, 100.0);

            if (windowStr.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            {
                weeklyWindow = new RateWindow(usedPercent, 10080, resetTime);
            }
            else if (windowStr.Equals("5h", StringComparison.OrdinalIgnoreCase))
            {
                fiveHourWindow = new RateWindow(usedPercent, 300, resetTime);
            }
        }

        if (weeklyWindow is null && fiveHourWindow is null)
        {
            return false;
        }

        snapshot = new RateLimitSnapshot(weeklyWindow, fiveHourWindow, null, eventTime);
        return true;
    }

    private sealed record DeduplicatedDatabase(string TrajectoryId, string FilePath, DateTime LastWriteTimeUtc);
    private sealed record ConversationCandidate(string TrajectoryId, UsageTotals Totals, DateTimeOffset StartedAt, DateTimeOffset LatestEventTime);

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
}

internal ref struct ProtobufSpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public ProtobufSpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool HasMore => _position < _buffer.Length;

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (!TryReadVarint(out ulong tag)) return false;
        wireType = (int)(tag & 0x07);
        fieldNumber = (int)(tag >> 3);
        return fieldNumber > 0;
    }

    public bool TryReadVarint(out ulong value)
    {
        value = 0;
        int shift = 0;
        int count = 0;
        while (_position < _buffer.Length && count < 10)
        {
            byte b = _buffer[_position++];
            count++;
            if (count == 10 && b > 1)
            {
                return false;
            }
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
        }
        return false;
    }

    public bool TryReadInt64(out long value)
    {
        value = 0;
        if (!TryReadVarint(out ulong raw)) return false;
        value = (long)raw;
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        value = 0;
        if (!TryReadVarint(out ulong raw)) return false;
        value = (int)raw;
        return true;
    }

    public bool TryReadLengthDelimited(out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(out ulong length)) return false;
        if (length > (ulong)(_buffer.Length - _position)) return false;
        int len = (int)length;
        slice = _buffer.Slice(_position, len);
        _position += len;
        return true;
    }

    public bool TrySkip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(out _);
            case 1:
                if (_position + 8 > _buffer.Length) return false;
                _position += 8;
                return true;
            case 2:
                if (!TryReadVarint(out ulong length)) return false;
                if (length > (ulong)(_buffer.Length - _position)) return false;
                _position += (int)length;
                return true;
            case 5:
                if (_position + 4 > _buffer.Length) return false;
                _position += 4;
                return true;
            default:
                return false;
        }
    }
}

internal sealed class WinSqliteDatabase : IDisposable
{
    private IntPtr _db = IntPtr.Zero;

    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_OPEN_URI = 0x00000040;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare16_v2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr pStmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text16", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text16(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr pStmt);

    public static WinSqliteDatabase OpenReadonly(string filePath)
    {
        var utf8Bytes = Encoding.UTF8.GetBytes(filePath + "\0");
        int rc = sqlite3_open_v2(utf8Bytes, out IntPtr db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }
            throw new IOException($"无法以只读方式打开 SQLite 数据库 (code {rc}): {filePath}");
        }
        sqlite3_busy_timeout(db, 2000);
        return new WinSqliteDatabase(db);
    }

    private WinSqliteDatabase(IntPtr db)
    {
        _db = db;
    }

    public string? ReadTrajectoryId()
    {
        if (_db == IntPtr.Zero) return null;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            int rc = sqlite3_prepare16_v2(_db, "SELECT trajectory_id FROM trajectory_meta LIMIT 1;", -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) return null;
            if (sqlite3_step(stmt) == SQLITE_ROW)
            {
                IntPtr textPtr = sqlite3_column_text16(stmt, 0);
                if (textPtr != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(textPtr);
                }
            }
            return null;
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                sqlite3_finalize(stmt);
            }
        }
    }

    public List<byte[]> ReadStepMetadataBlobs()
    {
        var list = new List<byte[]>();
        if (_db == IntPtr.Zero) return list;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            int rc = sqlite3_prepare16_v2(_db, "SELECT metadata FROM steps WHERE metadata IS NOT NULL ORDER BY idx ASC;", -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) return list;
            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                int bytes = sqlite3_column_bytes(stmt, 0);
                if (bytes <= 0 || bytes > AntigravityUsageReader.MaxMetadataBlobBytes) continue;
                IntPtr blobPtr = sqlite3_column_blob(stmt, 0);
                if (blobPtr == IntPtr.Zero) continue;
                byte[] buffer = new byte[bytes];
                Marshal.Copy(blobPtr, buffer, 0, bytes);
                list.Add(buffer);
            }
            return list;
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                sqlite3_finalize(stmt);
            }
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
