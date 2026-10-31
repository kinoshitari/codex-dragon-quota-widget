using System.Text.Json;

namespace DragonQuotaWidget;

/// <summary>Bounded, per-turn token_count measurements for the usage status line.</summary>
public sealed class CodexTurnMetrics
{
    public const int SampleLimit = 24;
    private readonly List<Sample> _samples = new();

    public void Clear() => _samples.Clear();

    public void ConfirmTurn(string? turnId)
    {
        if (string.IsNullOrEmpty(turnId)) return;
        for (var index = _samples.Count - 1; index >= 0 && string.IsNullOrEmpty(_samples[index].TurnId); index--)
            _samples[index] = _samples[index] with { TurnId = turnId };
    }

    public void Observe(string? turnId, DateTimeOffset? at, JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("last_token_usage", out var last) || last.ValueKind != JsonValueKind.Object) return;
        var input = Count(last, "input_tokens");
        if (input <= 0 && Count(last, "output_tokens") <= 0 && Count(last, "total_tokens") <= 0) return;
        var total = info.TryGetProperty("total_token_usage", out var value) ? value : default;
        _samples.Add(new(turnId, at, input, Count(last, "cached_input_tokens"), Count(total, "output_tokens")));
        if (_samples.Count > SampleLimit) _samples.RemoveAt(0);
    }

    public CodexTurnUsage? Snapshot(DateTimeOffset now)
    {
        if (_samples.Count == 0) return null;
        var latest = _samples[^1];
        var first = _samples.Count - 1;
        // A monotonic session counter is NOT a turn boundary. Without a real
        // turn ID only the latest call's cache ratio is known; TPS is unknown.
        if (!string.IsNullOrEmpty(latest.TurnId))
            while (first > 0 && _samples[first - 1].TurnId == latest.TurnId) first--;

        long input = 0, cached = 0, output = 0;
        double seconds = 0;
        for (var index = first; index < _samples.Count; index++)
        {
            var current = _samples[index];
            if (current.Input > 0)
            {
                input += current.Input;
                cached += Math.Clamp(current.Cached, 0, current.Input);
            }
            if (index == first) continue;
            var previous = _samples[index - 1];
            if (current.TotalOutput <= previous.TotalOutput || previous.At is not { } start || current.At is not { } end) continue;
            var interval = (end - start).TotalSeconds;
            if (interval < 0.5 || interval > 120) continue;
            output += current.TotalOutput - previous.TotalOutput;
            seconds += interval;
        }
        return new(latest.TurnId, input, cached, output, seconds, latest.At,
            latest.At is { } updated && now - updated > TimeSpan.FromMinutes(2));
    }

    private static long Count(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) ? number : 0;

    private sealed record Sample(string? TurnId, DateTimeOffset? At, long Input, long Cached, long TotalOutput);
}

public sealed record CodexTurnUsage(string? TurnId, long InputTokens, long CachedInputTokens,
    long MeasuredOutputTokens, double MeasuredSeconds, DateTimeOffset? UpdatedAt, bool IsSpeedStale)
{
    public double? TokensPerSecond => MeasuredSeconds > 0 ? MeasuredOutputTokens / MeasuredSeconds : null;
    public double? CacheHitRate => InputTokens > 0 ? (double)CachedInputTokens / InputTokens : null;
}
