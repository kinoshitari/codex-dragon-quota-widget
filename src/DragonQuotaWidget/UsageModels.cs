using System.Text.Json.Serialization;

namespace DragonQuotaWidget;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageSurface { Codex, Work, Agy }

public sealed record UsageSnapshot(
    UsageBySurface Today,
    UsageBySurface Last24Hours,
    UsageBySurface Last7Days,
    UsageBySurface Last30Days,
    UsageBySurface AllTime,
    ConversationUsage? CurrentConversation,
    RateLimitSnapshot? RateLimits,
    DateTimeOffset ReadAt,
    string? Warning)
{
    public static UsageSnapshot Empty(DateTimeOffset now, string warning) => new(
        UsageBySurface.Empty,
        UsageBySurface.Empty,
        UsageBySurface.Empty,
        UsageBySurface.Empty,
        UsageBySurface.Empty,
        null,
        null,
        now,
        warning);
}

public sealed record UsageBySurface
{
    public UsageTotals Codex { get; init; }
    public UsageTotals Work { get; init; }
    public UsageTotals Agy { get; init; }

    public UsageBySurface(UsageTotals codex, UsageTotals work) : this(codex, work, UsageTotals.Empty)
    {
    }

    [JsonConstructor]
    public UsageBySurface(UsageTotals? codex, UsageTotals? work, UsageTotals? agy)
    {
        Codex = codex ?? UsageTotals.Empty;
        Work = work ?? UsageTotals.Empty;
        Agy = agy ?? UsageTotals.Empty;
    }

    public static UsageBySurface Empty { get; } = new(UsageTotals.Empty, UsageTotals.Empty, UsageTotals.Empty);
    public UsageTotals Total => Codex + Work + Agy;
}

public sealed record UsageTotals(long InputTokens, long OutputTokens, long CachedInputTokens, long ReasoningOutputTokens)
{
    public static UsageTotals Empty { get; } = new(0, 0, 0, 0);
    public long TotalTokens => InputTokens + OutputTokens;
    public double CacheHitRate => InputTokens <= 0 ? 0d : Math.Clamp((double)CachedInputTokens / InputTokens, 0d, 1d);
    public static UsageTotals operator +(UsageTotals left, UsageTotals right) => new(
        left.InputTokens + right.InputTokens,
        left.OutputTokens + right.OutputTokens,
        left.CachedInputTokens + right.CachedInputTokens,
        left.ReasoningOutputTokens + right.ReasoningOutputTokens);
}

public sealed record ConversationUsage(string Id, UsageSurface Surface, UsageTotals Tokens, DateTimeOffset StartedAt);
public sealed record RateLimitSnapshot(RateWindow? Primary, RateWindow? Secondary, CreditSnapshot? Credits, DateTimeOffset EventAt);
public sealed record RateWindow(double UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt);
public sealed record CreditSnapshot(bool HasCredits, bool Unlimited, string? Balance);
