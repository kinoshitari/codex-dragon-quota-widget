using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonQuotaWidget;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetMode { Quota, FiveHourQuota, Summary, Today, Conversation }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeftClickDisplayMode
{
    CodexQuota,
    AgyQuota,
    DoubaoQuota,
    Interaction,
    // Backward compatibility for v2 settings
    QuotaInfo = CodexQuota
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageSource
{
    Codex,
    Agy,
    Doubao
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenTimeRange { Today, Last24Hours }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionSoundSet { Duck, Effect1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SummaryTimeRange
{
    Last7Days,
    Last30Days,
    AllTime,
    // Preserve compatibility with settings saved by earlier builds.
    Week = Last7Days,
    Month = Last30Days
}

public sealed class WidgetSettings
{
    private const int CurrentSchemaVersion = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WidgetMode Mode { get; set; } = WidgetMode.Quota;
    public LeftClickDisplayMode LeftClickMode { get; set; } = LeftClickDisplayMode.Interaction;
    public UsageSource UsageSource { get; set; } = UsageSource.Codex;
    public int SettingsSchemaVersion { get; set; }
    public TokenTimeRange TokenTimeRange { get; set; } = TokenTimeRange.Today;
    public SummaryTimeRange SummaryTimeRange { get; set; } = SummaryTimeRange.Last7Days;
    public bool StartWithCodex { get; set; } = true;
    public bool PinInfoPanel { get; set; }
    public bool LockPosition { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool MinimizeOnClose { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public InteractionSoundSet SoundSet { get; set; } = InteractionSoundSet.Duck;
    public double SoundVolume { get; set; } = 0.55;
    public double ResetInteractionLockSeconds { get; set; } = 3;
    public double InfoPanelDisplaySeconds { get; set; } = 5;
    public bool ShowCodexActivityBubble { get; set; } = true;
    public double Scale { get; set; } = 0.9;
    public double? Left { get; set; }
    public double? Top { get; set; }

    public static WidgetSettings Load(string? pathOverride = null)
    {
        var path = pathOverride ?? GetSettingsPath();
        try
        {
            var settings = File.Exists(path)
                ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(path), JsonOptions) ?? new WidgetSettings()
                : new WidgetSettings();
            if (settings.SettingsSchemaVersion < 2)
            {
                // Version 2 changes the default from a permanently visible,
                // Migrate older permanently visible layouts to a freely
                // positioned character whose information panel appears only
                // when needed.
                settings.PinInfoPanel = false;
            }
            if (settings.SettingsSchemaVersion < CurrentSchemaVersion)
            {
                if (settings.LeftClickMode == LeftClickDisplayMode.CodexQuota)
                {
                    settings.UsageSource = UsageSource.Codex;
                }
                else if (settings.LeftClickMode == LeftClickDisplayMode.AgyQuota)
                {
                    settings.UsageSource = UsageSource.Agy;
                }
                else if (settings.LeftClickMode == LeftClickDisplayMode.DoubaoQuota)
                {
                    settings.UsageSource = UsageSource.Doubao;
                }
                settings.SettingsSchemaVersion = CurrentSchemaVersion;
            }

            settings.SoundVolume = Math.Clamp(settings.SoundVolume, 0d, 1d);
            settings.ResetInteractionLockSeconds = Math.Clamp(settings.ResetInteractionLockSeconds, 1d, 15d);
            settings.InfoPanelDisplaySeconds = Math.Clamp(settings.InfoPanelDisplaySeconds, 2d, 30d);
            settings.Scale = Math.Clamp(settings.Scale, 0.5d, 1.8d);
            return settings;
        }
        catch
        {
            BackupCorruptSettings(path);
            return new WidgetSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexDragonQuotaWidget",
        "settings.json");

    private static void BackupCorruptSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backupPath = $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmssfff}.bak";
            File.Move(path, backupPath);
        }
        catch
        {
            // Loading defaults must remain possible even when backup creation fails.
        }
    }
}
