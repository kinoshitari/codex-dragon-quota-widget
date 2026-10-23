using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DragonQuotaWidget.Tests;

public static class Program
{
    private static int _passedTests;
    private static int _failedTests;
    private static readonly AntigravityUsageReader.CommandRunner NoOpQuotaRunner = (_, _, _) => (0, "{}", string.Empty);

    public static int Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" Running DragonQuotaWidget AGY Data Layer Tests ");
        Console.WriteLine("=================================================");

        var testDir = Path.Combine(Path.GetTempPath(), "DragonQuotaWidget_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            if (args.Contains("--ui-preview"))
            {
                RunTest("UI: Activity, completion and quota layout", TestPanelMirrorLayout);
                return _failedTests == 0 ? 0 : 1;
            }
            RunTest("Protobuf: Basic tokens and cached/reasoning semantics", TestProtobufParsing_BasicTokensAndSemantics);
            RunTest("Protobuf: Reasoning clamping and field invariant", TestProtobufParsing_ReasoningClamping);
            RunTest("Protobuf: Non-token events without field 9 skipped", TestProtobufParsing_NonTokenEventSkipped);
            RunTest("Protobuf: Token events without timestamp skipped", TestProtobufParsing_MissingTimestampSkipped);
            RunTest("Protobuf: Malformed bytes and corrupt varint tolerance", TestProtobufParsing_MalformedTolerance);
            RunTest("Storage: Trajectory deduplication by ID picks newest last-write file", () => TestTrajectoryDeduplication_PicksNewestFile(testDir));
            RunTest("Storage: Corrupt newest trajectory falls back to older valid copy", () => TestTrajectoryDeduplication_FallsBackFromCorruptNewest(testDir));
            RunTest("Storage: Period aggregation across Today, 24h, 7d, 30d, AllTime and surface isolation", () => TestPeriodAggregation_AllPeriodsAndSurfaces(testDir));
            RunTest("Storage: Current conversation resolved by latest event timestamp", () => TestCurrentConversation_PicksLatestByEventTimestamp(testDir));
            RunTest("Storage: Malformed and empty database tolerance without crashing", () => TestMalformedDatabaseTolerance(testDir));
            RunTest("Quota: Full JSON parsing for Gemini weekly (10080m) and 5h (300m) buckets", TestQuotaParsing_FullResponse);
            RunTest("Quota: Missing window behavior (do not infer absent windows)", TestQuotaParsing_MissingWindows);
            RunTest("Quota: Multiline stdout with preamble logs parsed correctly", TestQuotaParsing_MultilineStdoutWithPreamble);
            RunTest("Quota: Multiple Gemini groups use the most constrained window", TestQuotaParsing_MultipleGeminiGroups);
            RunTest("Quota: Fresh cache, stale fallback, and hard expiry", TestQuotaCommandRunner_Caching);
            RunTest("Codex: Quota discovery is not limited to the newest eight files", () => TestCodexQuotaDiscovery_BeyondNewestEight(testDir));
            RunTest("Codex: Cumulative deltas, duplicates, 24h boundary and newest copy", () => TestCodexTokenAccounting(testDir));
            RunTest("Codex: Resumed conversation retains earlier history outside timed windows", () => TestResumedConversationHistory(testDir));
            RunTest("Placement: Quota panel mirrors inward and retains the dragon anchor", TestPanelMirrorLayout);
            RunTest("Activity: Parallel session completion is observed", () => TestActivityMonitor_ParallelSessions(testDir));
            RunTest("Activity: Public summaries, model, response TPS and task reset", () => TestActivitySummaryAndRate(testDir));
            RunTest("Activity: TPS timing, response deduplication and model isolation", () => TestActivityRateBoundaries(testDir));
            RunTest("Activity: TPS recovers before the initial tail and resets on truncation", () => TestActivityRateReplay(testDir));
            RunTest("Models: Existing Codex/Work calculation and serialization compatibility", TestUsageModels_CodexCompatibility);
            RunTest("Settings: Legacy QuotaInfo mode maps to Codex quota", TestSettings_LegacyQuotaInfoAlias);
            RunTest("Claude: Quota cache, missing buckets and expiry", TestClaudeQuotaParsing);
            RunTest("Claude: Source toggle and legacy settings migration", () => TestClaudeSourceCycle(testDir));
            RunTest("Claude: Transcript usage, progress, model, speed and completion", () => TestClaudeTranscript(testDir));
            RunTest("Settings: Corrupt JSON is backed up before defaults are restored", () => TestSettings_CorruptFileBackup(testDir));
            RunTest("Placement: Dragon faces inward on both halves of the work area", TestPlacement_FacesInward);

            Console.WriteLine("=================================================");
            Console.WriteLine($" Test Results: {_passedTests} passed, {_failedTests} failed.");
            Console.WriteLine("=================================================");

            return _failedTests == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDir))
                {
                    Directory.Delete(testDir, recursive: true);
                }
            }
            catch { }
        }
    }

    private static void RunTest(string testName, Action testAction)
    {
        try
        {
            testAction();
            Console.WriteLine($" [PASS] {testName}");
            _passedTests++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" [FAIL] {testName}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            _failedTests++;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception($"Assertion failed: {message}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Assertion failed ({message}): expected '{expected}', actual '{actual}'");
        }
    }

    private static void TestProtobufParsing_BasicTokensAndSemantics()
    {
        var timestamp = new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.FromHours(8));
        long nonCachedInput = 1000;
        long totalOutput = 500;
        long cachedInput = 250;
        long visibleOutput = 300;
        long reasoningOutput = 200;

        var blob = SyntheticProtobuf.CreateStepMetadata(
            timestamp,
            nonCachedInput: nonCachedInput,
            totalOutput: totalOutput,
            cachedInput: cachedInput,
            visibleOutput: visibleOutput,
            reasoningOutput: reasoningOutput);

        bool success = AntigravityUsageReader.TryParseStepMetadata(blob, out var parsedTimestamp, out var usage);
        Assert(success, "Should successfully parse valid step metadata");
        AssertEqual(timestamp.ToUnixTimeSeconds(), parsedTimestamp.ToUnixTimeSeconds(), "Timestamp seconds should match");

        // InputTokens = nonCachedInput (1000) + cachedInput (250) = 1250
        AssertEqual(1250L, usage.InputTokens, "InputTokens must be field2 + field5");
        // OutputTokens = totalOutput (500)
        AssertEqual(500L, usage.OutputTokens, "OutputTokens must be field3");
        // CachedInputTokens = cachedInput (250)
        AssertEqual(250L, usage.CachedInputTokens, "CachedInputTokens must be field5");
        // ReasoningOutputTokens = min(reasoningOutput (200), totalOutput (500)) = 200
        AssertEqual(200L, usage.ReasoningOutputTokens, "ReasoningOutputTokens must be field10");
        AssertEqual(1750L, usage.TotalTokens, "TotalTokens must be Input + Output");
        Assert(Math.Abs(usage.CacheHitRate - (250.0 / 1250.0)) < 1e-6, "Cache hit rate must be Cached / Input");
    }

    private static void TestProtobufParsing_ReasoningClamping()
    {
        var timestamp = DateTimeOffset.UtcNow;
        // Case: reasoning tokens reported larger than total output tokens
        long nonCachedInput = 100;
        long totalOutput = 200;
        long reasoningOutput = 300; // Malformed: exceeds total output

        var blob = SyntheticProtobuf.CreateStepMetadata(
            timestamp,
            nonCachedInput: nonCachedInput,
            totalOutput: totalOutput,
            reasoningOutput: reasoningOutput);

        bool success = AntigravityUsageReader.TryParseStepMetadata(blob, out _, out var usage);
        Assert(success, "Should parse step metadata even if reasoning is oversized");
        AssertEqual(200L, usage.OutputTokens, "OutputTokens must match field3");
        AssertEqual(200L, usage.ReasoningOutputTokens, "ReasoningOutputTokens must be clamped to OutputTokens");
    }

    private static void TestProtobufParsing_NonTokenEventSkipped()
    {
        var timestamp = DateTimeOffset.UtcNow;
        // Step metadata without field 9 (generation metadata)
        var blob = SyntheticProtobuf.CreateStepMetadata(
            timestamp,
            nonCachedInput: 100,
            totalOutput: 200,
            includeGeneration: false);

        bool success = AntigravityUsageReader.TryParseStepMetadata(blob, out _, out var usage);
        Assert(!success, "Rows without field 9 must not be treated as token events");
        AssertEqual(UsageTotals.Empty, usage, "Usage should be empty");
    }

    private static void TestProtobufParsing_MissingTimestampSkipped()
    {
        // Field 9 contains a valid generation message, but field 1 timestamp is absent.
        byte[] generation = SyntheticProtobuf.EncodeGenerationMetadata(
            nonCachedInput: 100,
            totalOutput: 20,
            cachedInput: 0,
            visibleOutput: 20,
            reasoningOutput: 0);
        byte[] blob = SyntheticProtobuf.WrapLengthDelimitedField(9, generation);

        bool success = AntigravityUsageReader.TryParseStepMetadata(blob, out _, out _);
        Assert(!success, "Token rows without a timestamp must be skipped instead of being assigned to Unix epoch");
    }

    private static void TestProtobufParsing_MalformedTolerance()
    {
        // 1. Truncated byte stream
        byte[] truncated = new byte[] { 0x0A, 0x05, 0x08 }; // Length says 5, only 1 byte follows
        bool success1 = AntigravityUsageReader.TryParseStepMetadata(truncated, out _, out _);
        Assert(!success1, "Truncated buffer should return false without throwing");

        // 2. Corrupt varint with all MSBs set
        byte[] infiniteVarint = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        bool success2 = AntigravityUsageReader.TryParseStepMetadata(infiniteVarint, out _, out _);
        Assert(!success2, "Exorbitant varint should return false without throwing");

        // 3. Empty buffer
        bool success3 = AntigravityUsageReader.TryParseStepMetadata(Array.Empty<byte>(), out _, out _);
        Assert(!success3, "Empty buffer should return false");

        // 4. Random garbage
        byte[] garbage = new byte[] { 0x7F, 0x88, 0x99, 0xAA, 0x01, 0x02, 0x03 };
        bool success4 = AntigravityUsageReader.TryParseStepMetadata(garbage, out _, out _);
        Assert(!success4, "Garbage buffer should return false without throwing");
    }

    private static void TestTrajectoryDeduplication_PicksNewestFile(string rootDir)
    {
        var testSubDir = Path.Combine(rootDir, "dedup_test_" + Guid.NewGuid().ToString("N"));
        var rootA = Path.Combine(testSubDir, "rootA");
        var rootB = Path.Combine(testSubDir, "rootB");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        var fileA = Path.Combine(rootA, "session_duplicate.db");
        var fileB = Path.Combine(rootB, "session_duplicate.db");

        var time1 = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.FromHours(8));
        var blobOld = SyntheticProtobuf.CreateStepMetadata(time1, nonCachedInput: 10, totalOutput: 10);
        var blobNew = SyntheticProtobuf.CreateStepMetadata(time1, nonCachedInput: 100, totalOutput: 100);

        // Create older file with 20 total tokens
        SyntheticDatabaseCreator.CreateDatabase(fileA, "traj-duplicate-id", new[] { (1L, (byte[]?)blobOld) });
        File.SetLastWriteTimeUtc(fileA, new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc));

        // Create newer file with 200 total tokens
        SyntheticDatabaseCreator.CreateDatabase(fileB, "traj-duplicate-id", new[] { (1L, (byte[]?)blobNew) });
        File.SetLastWriteTimeUtc(fileB, new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc));

        var reader = new AntigravityUsageReader(
            customRoots: new[] { rootA, rootB },
            clock: () => time1.AddHours(2),
            quotaRunner: NoOpQuotaRunner);

        var snapshot = reader.ReadSnapshot();
        AssertEqual(200L, snapshot.AllTime.Total.TotalTokens, "Deduplication must select the newer file (200 tokens) and discard the older (20 tokens)");
        AssertEqual(100L, snapshot.AllTime.Agy.InputTokens, "Agy input tokens must match newer file");
        AssertEqual(100L, snapshot.AllTime.Agy.OutputTokens, "Agy output tokens must match newer file");
    }

    private static void TestTrajectoryDeduplication_FallsBackFromCorruptNewest(string rootDir)
    {
        var testSubDir = Path.Combine(rootDir, "dedup_fallback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testSubDir);
        var older = Path.Combine(testSubDir, "older.db");
        var newer = Path.Combine(testSubDir, "newer.db");
        var now = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.FromHours(8));
        var valid = SyntheticProtobuf.CreateStepMetadata(now.AddMinutes(-5), nonCachedInput: 120, totalOutput: 30);

        SyntheticDatabaseCreator.CreateDatabase(older, "traj-fallback", new[] { (1L, (byte[]?)valid) });
        File.SetLastWriteTimeUtc(older, now.UtcDateTime.AddMinutes(-2));
        SyntheticDatabaseCreator.CreateDatabase(newer, "traj-fallback", new[] { (1L, (byte[]?)new byte[] { 0xFF, 0xFF, 0xFF }) });
        File.SetLastWriteTimeUtc(newer, now.UtcDateTime.AddMinutes(-1));

        var snapshot = new AntigravityUsageReader(
            customRoots: new[] { testSubDir },
            clock: () => now,
            quotaRunner: NoOpQuotaRunner).ReadSnapshot();

        AssertEqual(150L, snapshot.AllTime.Agy.TotalTokens, "A corrupt newest copy must fall back to the older valid trajectory");
        Assert(snapshot.Warning?.Contains("回退", StringComparison.Ordinal) == true, "Fallback must be disclosed in warnings");
    }

    private static void TestPeriodAggregation_AllPeriodsAndSurfaces(string rootDir)
    {
        var testSubDir = Path.Combine(rootDir, "period_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testSubDir);
        var dbPath = Path.Combine(testSubDir, "periods.db");

        // Fixed clock: 2026-08-27 18:00:00 +08:00 (Local time)
        var now = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.FromHours(8));

        // 1. Today (e.g. 2 hours ago on 2026-08-27 16:00) -> 100 input, 50 output = 150
        var t1 = now.AddHours(-2);
        var b1 = SyntheticProtobuf.CreateStepMetadata(t1, nonCachedInput: 100, totalOutput: 50, cachedInput: 20);

        // 2. Rolling 24h but yesterday (e.g. 2026-08-26 23:00, 19 hours ago) -> 200 input, 100 output = 300
        var t2 = now.AddHours(-19);
        var b2 = SyntheticProtobuf.CreateStepMetadata(t2, nonCachedInput: 200, totalOutput: 100);

        // 3. 3 days ago (2026-08-24 12:00) -> in 7d, 30d, AllTime -> 300 input, 150 output = 450
        var t3 = now.AddDays(-3);
        var b3 = SyntheticProtobuf.CreateStepMetadata(t3, nonCachedInput: 300, totalOutput: 150);

        // 4. 15 days ago (2026-08-12 12:00) -> in 30d, AllTime -> 400 input, 200 output = 600
        var t4 = now.AddDays(-15);
        var b4 = SyntheticProtobuf.CreateStepMetadata(t4, nonCachedInput: 400, totalOutput: 200);

        // 5. 45 days ago (2026-07-13 12:00) -> AllTime only -> 500 input, 250 output = 750
        var t5 = now.AddDays(-45);
        var b5 = SyntheticProtobuf.CreateStepMetadata(t5, nonCachedInput: 500, totalOutput: 250);

        // 6. Future event > now + 5 min (e.g. 2 days in future) -> excluded from rolling windows and today, in AllTime -> 600 input, 300 output = 900
        var t6 = now.AddDays(2);
        var b6 = SyntheticProtobuf.CreateStepMetadata(t6, nonCachedInput: 600, totalOutput: 300);

        var steps = new (long, byte[]?)[]
        {
            (1, b1),
            (2, b2),
            (3, b3),
            (4, b4),
            (5, b5),
            (6, b6)
        };

        SyntheticDatabaseCreator.CreateDatabase(dbPath, "traj-periods", steps);

        var reader = new AntigravityUsageReader(
            customRoots: new[] { testSubDir },
            clock: () => now,
            quotaRunner: NoOpQuotaRunner);

        var snapshot = reader.ReadSnapshot();

        // 1. Today: only b1 (100+20 in, 50 out = 170 total)
        AssertEqual(120L, snapshot.Today.Agy.InputTokens, "Today Agy InputTokens");
        AssertEqual(50L, snapshot.Today.Agy.OutputTokens, "Today Agy OutputTokens");
        AssertEqual(170L, snapshot.Today.Total.TotalTokens, "Today Total tokens");
        AssertEqual(0L, snapshot.Today.Codex.TotalTokens, "Today Codex tokens must be zero");
        AssertEqual(0L, snapshot.Today.Work.TotalTokens, "Today Work tokens must be zero");

        // 2. Last24Hours: b1 + b2 (b1: 120in/50out=170, b2: 200in/100out=300 -> 470 total)
        AssertEqual(320L, snapshot.Last24Hours.Agy.InputTokens, "Last24Hours Agy InputTokens");
        AssertEqual(150L, snapshot.Last24Hours.Agy.OutputTokens, "Last24Hours Agy OutputTokens");
        AssertEqual(470L, snapshot.Last24Hours.Total.TotalTokens, "Last24Hours Total tokens");

        // 3. Last7Days: b1 + b2 + b3 (b3: 300in/150out=450 -> 470 + 450 = 920 total)
        AssertEqual(620L, snapshot.Last7Days.Agy.InputTokens, "Last7Days Agy InputTokens");
        AssertEqual(300L, snapshot.Last7Days.Agy.OutputTokens, "Last7Days Agy OutputTokens");
        AssertEqual(920L, snapshot.Last7Days.Total.TotalTokens, "Last7Days Total tokens");

        // 4. Last30Days: b1 + b2 + b3 + b4 (b4: 400in/200out=600 -> 920 + 600 = 1520 total)
        AssertEqual(1020L, snapshot.Last30Days.Agy.InputTokens, "Last30Days Agy InputTokens");
        AssertEqual(500L, snapshot.Last30Days.Agy.OutputTokens, "Last30Days Agy OutputTokens");
        AssertEqual(1520L, snapshot.Last30Days.Total.TotalTokens, "Last30Days Total tokens");

        // 5. AllTime: b1 + b2 + b3 + b4 + b5 + b6 (1520 + 750 + 900 = 3170 total)
        AssertEqual(2120L, snapshot.AllTime.Agy.InputTokens, "AllTime Agy InputTokens");
        AssertEqual(1050L, snapshot.AllTime.Agy.OutputTokens, "AllTime Agy OutputTokens");
        AssertEqual(3170L, snapshot.AllTime.Total.TotalTokens, "AllTime Total tokens");
    }

    private static void TestCurrentConversation_PicksLatestByEventTimestamp(string rootDir)
    {
        var testSubDir = Path.Combine(rootDir, "conv_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testSubDir);

        var now = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.FromHours(8));

        // Trajectory 1: events at 08:00 and 09:00
        var t1_start = now.AddHours(-10);
        var t1_end = now.AddHours(-9);
        var b1_1 = SyntheticProtobuf.CreateStepMetadata(t1_start, nonCachedInput: 50, totalOutput: 25);
        var b1_2 = SyntheticProtobuf.CreateStepMetadata(t1_end, nonCachedInput: 50, totalOutput: 25);
        var db1 = Path.Combine(testSubDir, "traj1.db");
        SyntheticDatabaseCreator.CreateDatabase(db1, "traj-older", new[] { (1L, (byte[]?)b1_1), (2L, (byte[]?)b1_2) });

        // Trajectory 2: events at 07:00 and 15:00 (latest event is at 15:00)
        var t2_start = now.AddHours(-11);
        var t2_end = now.AddHours(-3);
        var b2_1 = SyntheticProtobuf.CreateStepMetadata(t2_start, nonCachedInput: 100, totalOutput: 50);
        var b2_2 = SyntheticProtobuf.CreateStepMetadata(t2_end, nonCachedInput: 200, totalOutput: 100);
        var db2 = Path.Combine(testSubDir, "traj2.db");
        SyntheticDatabaseCreator.CreateDatabase(db2, "traj-latest", new[] { (1L, (byte[]?)b2_1), (2L, (byte[]?)b2_2) });

        var reader = new AntigravityUsageReader(
            customRoots: new[] { testSubDir },
            clock: () => now,
            quotaRunner: NoOpQuotaRunner);

        var snapshot = reader.ReadSnapshot();
        Assert(snapshot.CurrentConversation is not null, "CurrentConversation should not be null");
        AssertEqual("traj-latest", snapshot.CurrentConversation!.Id, "Current conversation must be the trajectory with the latest event timestamp");
        AssertEqual(UsageSurface.Agy, snapshot.CurrentConversation.Surface, "Current conversation surface must be Agy");
        AssertEqual(450L, snapshot.CurrentConversation.Tokens.TotalTokens, "Current conversation total tokens (150 + 300 = 450)");
        AssertEqual(t2_start.ToUnixTimeSeconds(), snapshot.CurrentConversation.StartedAt.ToUnixTimeSeconds(), "Current conversation StartedAt must be earliest event timestamp in that trajectory");
    }

    private static void TestMalformedDatabaseTolerance(string rootDir)
    {
        var testSubDir = Path.Combine(rootDir, "malformed_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testSubDir);

        // 1. Valid database
        var now = DateTimeOffset.UtcNow;
        var validDb = Path.Combine(testSubDir, "valid.db");
        var validBlob = SyntheticProtobuf.CreateStepMetadata(now, nonCachedInput: 100, totalOutput: 50);
        SyntheticDatabaseCreator.CreateDatabase(validDb, "traj-valid", new[] { (1L, (byte[]?)validBlob) });

        // 2. Corrupt / 0-byte database
        var corruptDb = Path.Combine(testSubDir, "corrupt.db");
        File.WriteAllBytes(corruptDb, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        // 3. Database without trajectory_meta table
        var incompleteDb = Path.Combine(testSubDir, "incomplete.db");
        File.WriteAllBytes(incompleteDb, Array.Empty<byte>());

        var reader = new AntigravityUsageReader(
            customRoots: new[] { testSubDir },
            clock: () => now,
            quotaRunner: NoOpQuotaRunner);

        var snapshot = reader.ReadSnapshot();
        AssertEqual(150L, snapshot.AllTime.Total.TotalTokens, "Valid database should still be read despite other corrupt files");
        Assert(!string.IsNullOrWhiteSpace(snapshot.Warning), "Warnings should capture corrupt file issues");
    }

    private static void TestQuotaParsing_FullResponse()
    {
        string json = @"{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Gemini Models"",
          ""buckets"": [
            {""id"":""gemini-weekly"",""window"":""weekly"",""remaining_fraction"":0.75,""reset_time"":""2026-09-01T13:25:59Z""},
            {""id"":""gemini-5h"",""window"":""5h"",""remaining_fraction"":0.9,""reset_time"":""2026-08-27T05:23:05Z""}
          ]
        }
      ]
    }
  }
}";

        var now = DateTimeOffset.UtcNow;
        var snapshot = AntigravityUsageReader.ParseQuotaJson(json, now);
        Assert(snapshot is not null, "Should parse valid quota JSON");

        // Primary = weekly
        Assert(snapshot!.Primary is not null, "Primary rate window should be weekly");
        AssertEqual(10080, snapshot.Primary!.WindowMinutes, "Weekly window must have WindowMinutes = 10080");
        Assert(Math.Abs(snapshot.Primary.UsedPercent - 25.0) < 1e-4, "Weekly UsedPercent must be (1 - 0.75) * 100 = 25.0");
        AssertEqual(DateTimeOffset.Parse("2026-09-01T13:25:59Z"), snapshot.Primary.ResetsAt, "Weekly reset time");

        // Secondary = 5h
        Assert(snapshot.Secondary is not null, "Secondary rate window should be 5h");
        AssertEqual(300, snapshot.Secondary!.WindowMinutes, "5h window must have WindowMinutes = 300");
        Assert(Math.Abs(snapshot.Secondary.UsedPercent - 10.0) < 1e-4, "5h UsedPercent must be (1 - 0.9) * 100 = 10.0");
        AssertEqual(DateTimeOffset.Parse("2026-08-27T05:23:05Z"), snapshot.Secondary.ResetsAt, "5h reset time");
    }

    private static void TestQuotaParsing_MissingWindows()
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Only weekly present
        string weeklyOnlyJson = @"{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Gemini Models"",
          ""buckets"": [
            {""id"":""gemini-weekly"",""window"":""weekly"",""remaining_fraction"":0.60,""reset_time"":""2026-09-01T13:25:59Z""}
          ]
        }
      ]
    }
  }
}";
        var snap1 = AntigravityUsageReader.ParseQuotaJson(weeklyOnlyJson, now);
        Assert(snap1 is not null, "Should parse weekly-only JSON");
        Assert(snap1!.Primary is not null, "Primary should be weekly");
        AssertEqual(10080, snap1.Primary!.WindowMinutes, "Weekly window minutes");
        Assert(snap1.Secondary is null, "Secondary should be null when 5h is absent (do not infer absent windows)");

        // 2. Only 5h present
        string fiveHourOnlyJson = @"{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Gemini Models"",
          ""buckets"": [
            {""id"":""gemini-5h"",""window"":""5h"",""remaining_fraction"":0.80,""reset_time"":""2026-08-27T05:23:05Z""}
          ]
        }
      ]
    }
  }
}";
        var snap2 = AntigravityUsageReader.ParseQuotaJson(fiveHourOnlyJson, now);
        Assert(snap2 is not null, "Should parse 5h-only JSON");
        Assert(snap2!.Primary is null, "Primary should be null when weekly is absent");
        Assert(snap2.Secondary is not null, "Secondary should be 5h");
        AssertEqual(300, snap2.Secondary!.WindowMinutes, "5h window minutes");

        // 3. Non-Gemini group
        string otherGroupJson = @"{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Claude Models"",
          ""buckets"": [
            {""id"":""claude-weekly"",""window"":""weekly"",""remaining_fraction"":0.80}
          ]
        }
      ]
    }
  }
}";
        var snap3 = AntigravityUsageReader.ParseQuotaJson(otherGroupJson, now);
        Assert(snap3 is null, "Non-Gemini groups should return null");
    }

    private static void TestQuotaParsing_MultilineStdoutWithPreamble()
    {
        string multilineStdout = @"[INFO] Starting AGY CLI 2.0
[DEBUG] Connecting to grpc endpoint...
[DEBUG] Token refreshed.
{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Gemini Models"",
          ""buckets"": [
            {""id"":""gemini-weekly"",""window"":""weekly"",""remaining_fraction"":0.50,""reset_time"":""2026-09-01T00:00:00Z""}
          ]
        }
      ]
    }
  }
}";

        var snapshot = AntigravityUsageReader.ParseQuotaJson(multilineStdout, DateTimeOffset.UtcNow);
        Assert(snapshot is not null, "Should locate and parse last JSON line in multiline output");
        Assert(snapshot!.Primary is not null, "Should find weekly rate window");
        Assert(Math.Abs(snapshot.Primary!.UsedPercent - 50.0) < 1e-4, "UsedPercent should be 50%");
    }

    private static void TestQuotaParsing_MultipleGeminiGroups()
    {
        string json = @"{
  ""status"": ""SUCCESS"",
  ""command"": { ""data"": { ""groups"": [
    { ""name"": ""Gemini Standard"", ""buckets"": [
      { ""window"": ""weekly"", ""remaining_fraction"": 0.80, ""reset_time"": ""2026-09-03T00:00:00Z"" }
    ] },
    { ""name"": ""Gemini Premium"", ""buckets"": [
      { ""window"": ""weekly"", ""remaining_fraction"": 0.25, ""reset_time"": ""2026-09-02T00:00:00Z"" },
      { ""window"": ""5h"", ""remaining_fraction"": 0.40 }
    ] }
  ] } }
}";

        var snapshot = AntigravityUsageReader.ParseQuotaJson(json, DateTimeOffset.UtcNow);
        Assert(snapshot?.Primary is not null, "Weekly quota should be parsed across all Gemini groups");
        var parsed = snapshot!;
        Assert(parsed.Secondary is not null, "5h quota should be parsed from a later Gemini group");
        Assert(Math.Abs(parsed.Primary!.UsedPercent - 75d) < 1e-4, "Most constrained weekly group should win");
        Assert(Math.Abs(parsed.Secondary!.UsedPercent - 60d) < 1e-4, "5h group should be retained");
    }

    private static void TestQuotaCommandRunner_Caching()
    {
        int invocationCount = 0;
        bool failRunner = false;
        AntigravityUsageReader.CommandRunner mockRunner = (file, args, timeout) =>
        {
            invocationCount++;
            if (failRunner) return (1, string.Empty, "simulated timeout");
            string json = @"{
  ""status"": ""SUCCESS"",
  ""command"": {
    ""name"": ""usage"",
    ""data"": {
      ""groups"": [
        {
          ""name"": ""Gemini Models"",
          ""buckets"": [
            {""id"":""gemini-5h"",""window"":""5h"",""remaining_fraction"":0.9}
          ]
        }
      ]
    }
  }
}";
            return (0, json, string.Empty);
        };

        var now = DateTimeOffset.UtcNow;
        var reader = new AntigravityUsageReader(
            customRoots: Array.Empty<string>(),
            clock: () => now,
            quotaRunner: mockRunner,
            quotaCacheDuration: TimeSpan.FromSeconds(30),
            maxStaleQuotaAge: TimeSpan.FromMinutes(5));

        var snap1 = reader.ReadSnapshot();
        AssertEqual(1, invocationCount, "First read should invoke quota runner");
        Assert(snap1.RateLimits is not null, "RateLimits should be populated");

        // Second call 10 seconds later (within 30s cache)
        now = now.AddSeconds(10);
        var snap2 = reader.ReadSnapshot();
        AssertEqual(1, invocationCount, "Second read within cache duration must NOT re-invoke runner");
        Assert(snap2.RateLimits is not null, "RateLimits should be returned from cache");

        // Third call 35 seconds later (past fresh cache duration): query fails,
        // so a clearly marked short-lived stale fallback is allowed.
        failRunner = true;
        now = now.AddSeconds(35);
        var snap3 = reader.ReadSnapshot();
        AssertEqual(2, invocationCount, "Read after cache expiry must re-invoke runner");
        Assert(snap3.RateLimits?.IsStale == true, "Failed refresh may only return a cache explicitly marked stale");
        Assert(!string.IsNullOrWhiteSpace(snap3.Warning), "Stale fallback must include a warning");

        // Once the maximum stale age is exceeded, no quota may be displayed.
        now = now.AddMinutes(6);
        var snap4 = reader.ReadSnapshot();
        AssertEqual(3, invocationCount, "Expired stale cache should trigger another query");
        Assert(snap4.RateLimits is null, "Expired stale quota must not be displayed");
        Assert(snap4.Warning?.Contains("已过期", StringComparison.Ordinal) == true, "Hard expiry must be disclosed");
    }

    private static void TestResumedConversationHistory(string rootDir)
    {
        var root = Path.Combine(rootDir, "resumed-history");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.Now;
        var meta = JsonSerializer.Serialize(new
        {
            timestamp = now.AddDays(-40), type = "session_meta",
            payload = new { id = "resumed", originator = "work_desktop", thread_source = "user" }
        });
        string Event(long total, long last, DateTimeOffset? at = null) => JsonSerializer.Serialize(new
        {
            timestamp = at ?? now.AddMinutes(-5), type = "event_msg", payload = new
            {
                type = "token_count", info = new
                {
                    total_token_usage = new { input_tokens = total, output_tokens = total / 10, cached_input_tokens = total / 2 },
                    last_token_usage = new { input_tokens = last, output_tokens = last / 10, cached_input_tokens = last / 2 }
                }
            }
        });
        var path = Path.Combine(sessions, "resumed.jsonl");
        File.WriteAllLines(path, new[] { meta, Event(1000, 100), Event(1500, 50), Event(1500, 50) });
        var reader = new CodexUsageReader(root);
        var snapshot = reader.ReadSnapshot();
        AssertEqual(new UsageTotals(1500, 150, 750, 0), snapshot.CurrentConversation!.Tokens, "Whole history includes the initial cumulative baseline");
        AssertEqual(new UsageTotals(600, 60, 300, 0), snapshot.Last24Hours.Work, "Earlier history is not charged to the last 24 hours");
        AssertEqual(snapshot.CurrentConversation.Tokens, snapshot.AllTime.Work, "All-time includes the same full history");
        var forkResumeMeta = JsonSerializer.Serialize(new
        {
            timestamp = now.AddDays(-2), type = "session_meta",
            payload = new { id = "resumed", originator = "work_desktop", thread_source = "user", forked_from_id = "source-thread" }
        });
        File.WriteAllLines(path, new[] { forkResumeMeta, Event(1000, 100), Event(1500, 50), Event(1500, 50) });
        var earlierMeta = JsonSerializer.Serialize(new
        {
            timestamp = now.AddDays(-45), type = "session_meta",
            payload = new { id = "resumed", originator = "work_desktop", thread_source = "user", forked_from_id = "source-thread" }
        });
        var earlierEvent = Event(2000, 2000, now.AddDays(-35));
        var fragment = Path.Combine(sessions, "earlier-fragment.jsonl");
        File.WriteAllLines(fragment, new[] { earlierMeta, earlierEvent });
        File.SetLastWriteTimeUtc(fragment, now.UtcDateTime.AddDays(-35));
        snapshot = reader.ReadSnapshot();
        AssertEqual(3500L, snapshot.CurrentConversation!.Tokens.InputTokens, "Separate rollout segments of the same thread are combined across resets");
        AssertEqual(now.AddDays(-45), snapshot.CurrentConversation.StartedAt, "Resuming a fork retains its original start time");
        var copy = Path.Combine(sessions, "fragment-copy.jsonl");
        File.Copy(fragment, copy);
        File.SetLastWriteTimeUtc(copy, now.UtcDateTime.AddDays(-36));
        AssertEqual(3500L, reader.ReadSnapshot().CurrentConversation!.Tokens.InputTokens, "A copied rollout does not count twice");
        File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddDays(-40));
        var other = Path.Combine(sessions, "other.jsonl");
        File.WriteAllText(other, meta.Replace("resumed", "other") + Environment.NewLine);
        AssertEqual(3500L, reader.ReadSnapshot().AllTime.Work.InputTokens, "Historical cached reader retains all rollout segments");
    }

    private static void TestPanelMirrorLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new System.Windows.Application();
                // Preview mode keeps window shutdown from saving test positions
                // to the user's real settings file.
                app.Properties["RenderPreviewPath"] = "layout-test";
                var window = new MainWindow();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
                void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
                var settings = (WidgetSettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
                settings.Scale = 1;
                settings.PinInfoPanel = false;
                window.Width = 390;
                window.Height = 440;
                window.Top = System.Windows.SystemParameters.WorkArea.Top + 10;
                window.Left = System.Windows.SystemParameters.WorkArea.Left + 20;
                var centerBefore = window.Left + 390 - 134;
                Call("UpdateDragonMirror");
                var panel = (System.Windows.FrameworkElement)window.FindName("DataPanel");
                var dragon = (System.Windows.FrameworkElement)window.FindName("DragonHost");
                AssertEqual(System.Windows.HorizontalAlignment.Right, panel.HorizontalAlignment, "Left-side quota panel belongs on the right");
                AssertEqual(System.Windows.HorizontalAlignment.Left, dragon.HorizontalAlignment, "Left-side artwork belongs on the left");
                AssertEqual(centerBefore, window.Left + 134, "Side change preserves character position");
                Set("_positionInitialized", true);
                Set("_temporaryInfoPanelVisible", true);
                Call("ApplyScale", 1d, false);
                Set("_temporaryInfoPanelVisible", false);
                Call("ApplyScale", 1d, false);
                AssertEqual(centerBefore, window.Left + 134, "Hiding the panel preserves character position");
                Set("_temporaryInfoPanelVisible", true);
                Call("ApplyScale", 1d, false);
                AssertEqual(centerBefore, window.Left + 134, "Showing the panel preserves character position");
                window.Left = System.Windows.SystemParameters.WorkArea.Right - 410;
                Call("UpdateDragonMirror");
                AssertEqual(System.Windows.HorizontalAlignment.Left, panel.HorizontalAlignment, "Right-side quota panel belongs on the left");
                AssertEqual(System.Windows.HorizontalAlignment.Right, dragon.HorizontalAlignment, "Right-side artwork belongs on the right");
                if (Environment.GetEnvironmentVariable("DRAGON_PREVIEW_DIR") is { Length: > 0 } previewDirectory)
                {
                    Directory.CreateDirectory(previewDirectory);
                    settings.UsageSource = UsageSource.Codex;
                    settings.ShowCodexActivityBubble = true;
                    Set("_codexIsWorking", true);
                    Set("_activitySource", UsageSource.Codex);
                    Set("_latestActivityModel", "6.1sol max · 近 50 t/s");
                    Call("UpdateActivityDetails");
                    AssertEqual("6.1sol max · 近 50 t/s", ((System.Windows.Controls.TextBlock)window.FindName("ActivityModelText")).Text, "Model/TPS belongs to the quota panel");
                    void Capture(string name, double width, double height)
                    {
                        var root = (System.Windows.FrameworkElement)window.FindName("RootGrid");
                        root.Measure(new System.Windows.Size(width, height));
                        root.Arrange(new System.Windows.Rect(0, 0, width, height));
                        root.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(previewDirectory, name));
                        encoder.Save(output);
                    }
                    panel.Visibility = System.Windows.Visibility.Visible;
                    Capture("quota.png", 390, 440);
                    panel.Visibility = System.Windows.Visibility.Collapsed;
                    Set("_temporaryInfoPanelVisible", false);
                    Call("ApplyScale", 1d, false);
                    Call("ShowActivityBubble", "正在核对会话历史与累计 Token，并调整额度提示框中的模型名称和速度显示。");
                    Set("_latestWorkDetail", "已运行命令 · exec_command");
                    Call("ShowActivityBubble", "正在核对会话历史与累计 Token，并调整额度提示框中的模型名称和速度显示。");
                    var bubble = (System.Windows.FrameworkElement)window.FindName("InteractionBubble");
                    bubble.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
                    bubble.Opacity = 1;
                    var transform = (System.Windows.Media.ScaleTransform)bubble.RenderTransform;
                    transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                    transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                    transform.ScaleX = transform.ScaleY = 1;
                    AssertEqual(390d, window.Width, "Bubble stays close to the character rather than far to the side");
                    Assert(window.Height > 300, "Bubble grows upward above the head");
                    Capture("summary-right.png", window.Width, window.Height);
                    var tipBottom = bubble.Margin.Top + bubble.ActualHeight;
                    Assert(Math.Abs(tipBottom - (window.Height - 300 + 28)) < 1, "Bubble tip is anchored near the upper horn");
                    window.Left = System.Windows.SystemParameters.WorkArea.Left + 20;
                    Call("UpdateDragonMirror");
                    AssertEqual(System.Windows.HorizontalAlignment.Right, bubble.HorizontalAlignment, "Left-side dragon puts the bubble on its right");
                    Capture("summary-left.png", window.Width, window.Height);
                    var leftAnchor = window.Left + 134;
                    var bottomAnchor = window.Top + window.Height;
                    Call("HideInteractionBubble", true, false);
                    AssertEqual(leftAnchor, window.Left + 134, "Closing the side bubble retains the left-side dragon anchor");
                    AssertEqual(bottomAnchor, window.Top + window.Height, "Closing the bubble retains the vertical character anchor");
                    settings.PinInfoPanel = true;
                    Set("_pendingCompletion", "已完成，挂件已更新并重启。中文进度下方显示工作提示；模型与最近一次生成速度位于额度框。");
                    Call("ShowCompletionIfNeeded");
                    AssertEqual(System.Windows.Visibility.Collapsed, panel.Visibility, "Pinned quota panel yields to completion");
                    var hint = (System.Windows.Controls.TextBlock)window.FindName("WorkHintText");
                    AssertEqual("✓ 任务完成", hint.Text, "Completion shows a separate footer");
                    bubble.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
                    bubble.Opacity = 1;
                    transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                    transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                    transform.ScaleX = transform.ScaleY = 1;
                    Capture("completion.png", window.Width, window.Height);
                    Call("HideInteractionBubble", true, false);
                    AssertEqual(System.Windows.Visibility.Visible, panel.Visibility, "Pinned panel is restored after completion");
                }
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void TestCodexTokenAccounting(string rootDir)
    {
        var root = Path.Combine(rootDir, "codex-accounting");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.Now;
        var meta = JsonSerializer.Serialize(new
        {
            timestamp = now.AddDays(-2), type = "session_meta",
            payload = new { id = "accounting", originator = "codex_desktop", thread_source = "user" }
        });
        string Event(DateTimeOffset at, long total, long? last) => JsonSerializer.Serialize(new
        {
            timestamp = at, type = "event_msg", payload = new
            {
                type = "token_count", info = new
                {
                    total_token_usage = new { input_tokens = total, output_tokens = total / 10, cached_input_tokens = total / 2, reasoning_output_tokens = total / 100 },
                    last_token_usage = last is null ? null : new { input_tokens = last.Value, output_tokens = last.Value / 10, cached_input_tokens = last.Value / 2, reasoning_output_tokens = last.Value / 100 }
                }
            }
        });
        var oldEvent = Event(now.AddHours(-25), 1000, 1000);
        var path = Path.Combine(sessions, "new.jsonl");
        File.WriteAllLines(path, new[] { meta, oldEvent,
            Event(now.AddHours(-2), 1000, 1000), // quota-only repeat must not enter 24h
            Event(now.AddHours(-1), 1500, 100), // last is incomplete, delta is 500
            Event(now.AddMinutes(-30), 1500, 100),
            Event(now.AddMinutes(-20), 1800, null) }); // cumulative-only update
        var older = Path.Combine(sessions, "old-copy.jsonl");
        File.WriteAllLines(older, new[] { meta, oldEvent });
        File.SetLastWriteTimeUtc(older, now.UtcDateTime.AddHours(-3));
        var reader = new CodexUsageReader(root);
        var snapshot = reader.ReadSnapshot();
        AssertEqual(new UsageTotals(800, 80, 400, 8), snapshot.Last24Hours.Codex, "Only cumulative growth within 24h counts");
        AssertEqual(new UsageTotals(1800, 180, 900, 18), snapshot.CurrentConversation!.Tokens, "Conversation uses full cumulative total from newest copy");
        AssertEqual(snapshot.CurrentConversation.Tokens, snapshot.AllTime.Codex, "Total and conversation agree");
        AssertEqual(snapshot.Last24Hours, reader.ReadSnapshot().Last24Hours, "Refresh is idempotent");
        File.AppendAllText(path, Event(now.AddMinutes(-10), 200, 200) + Environment.NewLine);
        AssertEqual(1000L, reader.ReadSnapshot().Last24Hours.Codex.InputTokens, "Counter restart adds new request only");
        AssertEqual(2000L, reader.ReadSnapshot().CurrentConversation!.Tokens.InputTokens, "Conversation retains usage before counter reset");

        var forkMeta = JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-5), type = "session_meta",
            payload = new { id = "fork", originator = "codex_desktop", thread_source = "user", forked_from_id = "accounting" }
        });
        var fork = Path.Combine(sessions, "fork.jsonl");
        File.WriteAllLines(fork, new[] { forkMeta, Event(now.AddMinutes(-4), 100000, 100), Event(now.AddMinutes(-3), 100200, 200) });
        File.SetLastWriteTimeUtc(fork, now.UtcDateTime.AddSeconds(2));
        snapshot = reader.ReadSnapshot();
        AssertEqual(300L, snapshot.CurrentConversation!.Tokens.InputTokens, "Fork must exclude inherited cumulative baseline");
        AssertEqual(1300L, snapshot.Last24Hours.Codex.InputTokens, "Fork adds only new usage to 24h");
        File.WriteAllLines(fork, new[] { forkMeta, Event(now.AddMinutes(-6), 99000, 99000), Event(now.AddMinutes(-4), 99100, 100) });
        AssertEqual(100L, reader.ReadSnapshot().CurrentConversation!.Tokens.InputTokens, "Fork replay before creation is not billed again");

        var archived = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(archived);
        File.Move(path, Path.Combine(archived, "new.jsonl"));
        snapshot = reader.ReadSnapshot();
        AssertEqual(1100L, snapshot.Last24Hours.Codex.InputTokens, "Archiving does not remove usage");
        AssertEqual(2100L, snapshot.AllTime.Codex.InputTokens, "All-time retains counter segments after archive");
        File.SetLastWriteTimeUtc(fork, now.UtcDateTime.AddDays(-40));
        File.SetLastWriteTimeUtc(Path.Combine(archived, "new.jsonl"), now.UtcDateTime.AddDays(-40));
        File.Delete(older);
        AssertEqual(2100L, reader.ReadSnapshot().AllTime.Codex.InputTokens, "Historical reader uses the same reset accounting");
    }

    private static void TestCodexQuotaDiscovery_BeyondNewestEight(string rootDir)
    {
        var codexRoot = Path.Combine(rootDir, "codex_quota_" + Guid.NewGuid().ToString("N"));
        var sessionsRoot = Path.Combine(codexRoot, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        var now = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.FromHours(8));

        for (var index = 0; index < 10; index++)
        {
            var sessionId = $"session-{index}";
            var path = Path.Combine(sessionsRoot, $"session-{index}.jsonl");
            var meta = JsonSerializer.Serialize(new
            {
                timestamp = now.AddDays(-60).AddMinutes(index),
                type = "session_meta",
                payload = new { id = sessionId, originator = "codex_desktop", thread_source = "user" }
            });
            object? rateLimits = index == 9
                ? new { primary = new { used_percent = 77d, window_minutes = 10080, resets_at = now.AddDays(1).ToUnixTimeSeconds() } }
                : null;
            var token = JsonSerializer.Serialize(new
            {
                timestamp = index == 9 ? now : now.AddDays(-60).AddMinutes(index),
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new { input_tokens = 10L, output_tokens = 5L, cached_input_tokens = 0L, reasoning_output_tokens = 0L },
                        total_token_usage = new { input_tokens = 10L, output_tokens = 5L, cached_input_tokens = 0L, reasoning_output_tokens = 0L }
                    },
                    rate_limits = rateLimits
                }
            });
            File.WriteAllLines(path, new[] { meta, token });
            File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddDays(-60).AddMinutes(-index));
        }

        var snapshot = new CodexUsageReader(codexRoot).ReadSnapshot();
        Assert(snapshot.RateLimits?.Primary is not null, "Quota should be discovered outside the newest eight files");
        var limits = snapshot.RateLimits!;
        Assert(Math.Abs(limits.Primary!.UsedPercent - 77d) < 1e-4, "The newest quota event by timestamp should win");
    }

    private static void TestActivitySummaryAndRate(string rootDir)
    {
        var root = Path.Combine(rootDir, "activity-summary");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var path = Path.Combine(root, "sessions", "task.jsonl");
        var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        string Record(int seconds, string type, object payload) => JsonSerializer.Serialize(new { timestamp = start.AddSeconds(seconds), type, payload });
        string Tokens(int seconds, int total, int last) => Record(seconds, "event_msg", new
        {
            type = "token_count", info = new { total_token_usage = new { output_tokens = total }, last_token_usage = new { output_tokens = last } }
        });
        File.WriteAllLines(path, new[] {
            Record(-1, "turn_context", new { model = "gpt-6.1-sol", effort = "max" }),
            Tokens(-1, 10000, 200),
            CreateActivityEvent(start, "task_started"),
            Record(1, "response_item", new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "正在核对日志字段，并准备调整额度框布局。" } }, encrypted_content = "never show this", content = "never show raw reasoning" }),
            TimedActivityItem(start, 2, 9, "reason1"),
            ResponseUsage(start.AddSeconds(12), "resp1", 500),
            Tokens(20, 10500, 500)
        });
        var monitor = new CodexActivityMonitor(root);
        var snapshot = monitor.Poll();
        AssertEqual("正在核对日志字段，并准备调整额度框布局。", snapshot.StatusText, "Public summary is shown");
        AssertEqual("gpt-6.1-sol", snapshot.Model, "Model comes from turn context");
        AssertEqual("max", snapshot.Effort, "Effort comes from turn context");
        AssertEqual(50d, snapshot.TokensPerSecond, "500 response tokens / ten generation seconds, excluding initial wait");
        AssertEqual("6.1sol max · 近 50 t/s", snapshot.ModelSpeedText, "The display identifies the latest response, not a live task average");
        File.AppendAllLines(path, new[] { Tokens(72, 10500, 500), Record(73, "response_item", new { type = "function_call", name = "exec_command", arguments = "never show tool input" }) });
        snapshot = monitor.Poll();
        AssertEqual(50d, snapshot.TokensPerSecond, "Sixty seconds of tool waiting and repeated counters must not change generation TPS");
        Assert(snapshot.StatusText.StartsWith("正在核对"), "Tool status does not erase the summary");
        File.AppendAllLines(path, new[] {
            Record(74, "event_msg", new { type = "agent_message", phase = "commentary", message = "已经找到字段，正在接入新的状态显示。" }),
            Record(74, "response_item", new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "**Considering internal options**" } } }),
            Record(74, "response_item", new { type = "function_call", name = "exec_command", call_id = "cmd1", arguments = "secret argument" }),
            Record(74, "response_item", new { type = "function_call_output", call_id = "cmd1", output = "private output" }) });
        snapshot = monitor.Poll();
        AssertEqual("已经找到字段，正在接入新的状态显示。", snapshot.StatusText, "English reasoning must not replace Chinese progress");
        AssertEqual("已运行命令 · exec_command", snapshot.WorkDetail, "Work hint is separate and does not echo arguments");
        File.AppendAllLines(path, new[] { CreateActivityEvent(start.AddSeconds(75), "task_complete"), CreateActivityEvent(start.AddSeconds(80), "task_started"), Record(81, "response_item", new { type = "reasoning", summary = Array.Empty<object>(), content = "never show raw reasoning" }) });
        snapshot = monitor.Poll();
        AssertEqual("开始处理任务…", snapshot.StatusText, "New task clears old summary; raw content is ignored");
        Assert(snapshot.TokensPerSecond is null, "New task clears old rate");
        File.AppendAllLines(path, new[] { TimedActivityItem(start, 82, 89, "reason2"), ResponseUsage(start.AddSeconds(92), "resp2", 200), Tokens(95, 10700, 200) });
        AssertEqual(20d, monitor.Poll().TokensPerSecond, "New task uses only its own response, including all output through response completion");
        File.AppendAllLines(path, new[] { Record(96, "event_msg", new { type = "agent_message", phase = "final", message = "已完成，挂件已更新。\n- 中文摘要和工作提示已经分开。" }), CreateActivityEvent(start.AddSeconds(97), "task_complete") });
        snapshot = monitor.Poll();
        Assert(snapshot.Model is null, "Idle display does not retain an active model");
        Assert(snapshot.CompletionText!.StartsWith("已完成，挂件已更新。"), "Completion retains the final public answer");
    }

    private static string TimedActivityItem(DateTimeOffset start, double from, double to, string id, string turn = "turn1", string type = "Reasoning") =>
        JsonSerializer.Serialize(new { timestamp = start.AddSeconds(to), type = "event_msg", payload = new {
            type = "item_completed", turn_id = turn, item = new { type, id },
            started_at_ms = start.AddSeconds(from).ToUnixTimeMilliseconds(), completed_at_ms = start.AddSeconds(to).ToUnixTimeMilliseconds() } });

    private static string ResponseUsage(DateTimeOffset at, string id, long tokens, string turn = "turn1") =>
        JsonSerializer.Serialize(new { timestamp = at, type = "token_usage_record", payload = new {
            turn_id = turn, response_id = id, usage = new { output_tokens = tokens, reasoning_output_tokens = tokens / 2 },
            turn_token_usage = new { output_tokens = 999999L }, thread_token_usage = new { output_tokens = 99999999L } } });

    private static void TestActivityRateBoundaries(string rootDir)
    {
        var root = Path.Combine(rootDir, "activity-rate");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var path = Path.Combine(root, "sessions", "task.jsonl");
        var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
        string Row(double seconds, string type, object payload) => JsonSerializer.Serialize(new { timestamp = start.AddSeconds(seconds), type, payload });
        File.WriteAllLines(path, new[] {
            Row(0, "event_msg", new { type = "task_started", turn_id = "turn1" }),
            Row(1, "turn_context", new { model = "gpt-6.1-sol", effort = "max", turn_id = "turn1" }),
            Row(10, "event_msg", new { type = "token_count", info = new { last_token_usage = new { output_tokens = 600 } } }),
            ResponseUsage(start.AddSeconds(12), "no-timing", 600) });
        var monitor = new CodexActivityMonitor(root);
        Assert(monitor.Poll().TokensPerSecond is null, "Task timestamps and usage alone are not a generation measurement");

        File.AppendAllLines(path, new[] {
            TimedActivityItem(start, 100, 106, "r1"),
            TimedActivityItem(start, 105, 108, "m1", type: "AgentMessage"),
            TimedActivityItem(start, 0, 111, "tool1", type: "CommandExecution"),
            ResponseUsage(start.AddSeconds(112), "response1", 600) });
        var snapshot = monitor.Poll();
        AssertEqual(50d, snapshot.TokensPerSecond, "Use the first generated item through response completion, not summed overlapping spans or tool time");
        AssertEqual(600L, snapshot.SpeedSample!.OutputTokens, "Reasoning is already included; cumulative usage is ignored");
        File.AppendAllLines(path, new[] {
            TimedActivityItem(start, 100, 106, "r1"),
            TimedActivityItem(start, 114, 116, "r2"),
            ResponseUsage(start.AddSeconds(117), "response1", 600),
            ResponseUsage(start.AddSeconds(118), "response2", 80) });
        snapshot = monitor.Poll();
        AssertEqual(20d, snapshot.TokensPerSecond, "Replayed item/response IDs do not contaminate the next response or reuse a task average");

        File.AppendAllText(path, Row(119, "turn_context", new { model = "gpt-6-astra", effort = "high", turn_id = "turn1" }) + Environment.NewLine);
        snapshot = monitor.Poll();
        AssertEqual("gpt-6-astra", snapshot.Model, "Model switch is observed");
        Assert(snapshot.TokensPerSecond is null, "A different model cannot inherit the previous speed");
        File.AppendAllLines(path, new[] {
            TimedActivityItem(start, 120, 121, "foreign", "other-turn"),
            TimedActivityItem(start, 122, 125, "r3"),
            ResponseUsage(start.AddSeconds(126), "foreign-response", 100000, "other-turn"),
            ResponseUsage(start.AddSeconds(127), "response3", 100) });
        AssertEqual(20d, monitor.Poll().TokensPerSecond, "A foreign turn must neither supply timing/tokens nor consume the current response boundary");

        // A malformed/unmeasured response consumes its own timing boundary.
        File.AppendAllLines(path, new[] {
            TimedActivityItem(start, 130, 131, "r4"),
            Row(132, "token_usage_record", new { usage = new { output_tokens = 100 } }),
            ResponseUsage(start.AddSeconds(150), "missing-start", 100) });
        Assert(monitor.Poll().TokensPerSecond is null, "Missing response ID cannot lend its start to a later response");
        File.AppendAllLines(path, new[] {
            Row(151, "event_msg", new { type = "item_completed", item = new { type = "Reasoning", id = "invalid" }, started_at_ms = "bad", completed_at_ms = 1 }),
            TimedActivityItem(start, 152, 152, "zero"),
            TimedActivityItem(start, 155, 154, "negative"),
            ResponseUsage(start.AddSeconds(156), "invalid-time", 100),
            TimedActivityItem(start, 157, 158, "r5"),
            Row(159, "token_usage_record", new { response_id = "bad-usage", usage = new { output_tokens = "bad" } }),
            TimedActivityItem(start, 160, 161, "r6"),
            JsonSerializer.Serialize(new { type = "token_usage_record", payload = new { response_id = "missing-timestamp", usage = new { output_tokens = 100 } } }),
            ResponseUsage(start.AddSeconds(170), "after-invalid", 100) });
        Assert(monitor.Poll().TokensPerSecond is null, "Invalid durations, counts and timestamps must not fabricate TPS or crash");

        var parallel = Path.Combine(root, "sessions", "parallel.jsonl");
        File.WriteAllLines(parallel, new[] {
            Row(180, "event_msg", new { type = "task_started", turn_id = "turn2" }),
            Row(181, "turn_context", new { model = "gpt-6.1-sol", effort = "low", turn_id = "turn2" }),
            TimedActivityItem(start, 182, 183, "parallel-r", "turn2"), ResponseUsage(start.AddSeconds(184), "parallel-response", 300, "turn2") });
        snapshot = monitor.Poll();
        AssertEqual(150d, snapshot.TokensPerSecond, "Parallel sessions keep separate generation windows");
        File.AppendAllText(parallel, Row(185, "event_msg", new { type = "task_complete", turn_id = "turn2" }) + Environment.NewLine);
        snapshot = monitor.Poll();
        AssertEqual("gpt-6-astra", snapshot.Model, "Completion returns to the still-working session");
        Assert(snapshot.TokensPerSecond is null, "The parallel session's speed must not leak into the selected model");
    }

    private static void TestActivityRateReplay(string rootDir)
    {
        var root = Path.Combine(rootDir, "activity-rate-replay");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var path = Path.Combine(root, "sessions", "task.jsonl");
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds());
        File.WriteAllLines(path, new[] {
            CreateActivityEvent(start, "task_started"), TimedActivityItem(start, 2, 9, "r1"), ResponseUsage(start.AddSeconds(12), "response1", 500),
            JsonSerializer.Serialize(new { type = "padding", text = new string('x', 600 * 1024) }) });
        var monitor = new CodexActivityMonitor(root);
        AssertEqual(50d, monitor.Poll().TokensPerSecond, "Initial-tail loading recovers the generation metadata from earlier context");
        File.WriteAllText(path, CreateActivityEvent(start.AddSeconds(80), "task_started") + Environment.NewLine);
        Assert(monitor.Poll().TokensPerSecond is null, "Truncation cannot retain a previous generation sample");
    }

    private static void TestActivityMonitor_ParallelSessions(string rootDir)
    {
        var codexRoot = Path.Combine(rootDir, "activity_" + Guid.NewGuid().ToString("N"));
        var sessionsRoot = Path.Combine(codexRoot, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        var firstPath = Path.Combine(sessionsRoot, "first.jsonl");
        var secondPath = Path.Combine(sessionsRoot, "second.jsonl");
        var now = DateTimeOffset.UtcNow;

        File.WriteAllText(firstPath, CreateActivityEvent(now, "task_started") + Environment.NewLine);
        File.WriteAllText(secondPath, CreateActivityEvent(now.AddSeconds(1), "agent_reasoning") + Environment.NewLine);
        File.SetLastWriteTimeUtc(firstPath, now.UtcDateTime);
        File.SetLastWriteTimeUtc(secondPath, now.UtcDateTime.AddSeconds(1));

        var monitor = new CodexActivityMonitor(codexRoot);
        var initial = monitor.Poll();
        Assert(initial.IsWorking, "At least one parallel session should be reported working");
        var initialCompletion = initial.CompletionRevision;

        File.AppendAllText(firstPath, CreateActivityEvent(now.AddSeconds(2), "task_complete") + Environment.NewLine);
        File.SetLastWriteTimeUtc(firstPath, now.UtcDateTime.AddSeconds(2));
        var afterCompletion = monitor.Poll();
        Assert(afterCompletion.IsWorking, "The second parallel session should remain working");
        Assert(afterCompletion.CompletionRevision > initialCompletion, "Completion from a non-selected session must be observed");
    }

    private static string CreateActivityEvent(DateTimeOffset timestamp, string eventType) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type = eventType }
    });

    private static void TestUsageModels_CodexCompatibility()
    {
        var codex = new UsageTotals(100, 50, 20, 10);
        var work = new UsageTotals(200, 100, 40, 20);

        // 1. Two-argument constructor preserves Codex and Work with empty Agy
        var surfaceUsage2 = new UsageBySurface(codex, work);
        AssertEqual(codex, surfaceUsage2.Codex, "Codex member preserved");
        AssertEqual(work, surfaceUsage2.Work, "Work member preserved");
        AssertEqual(UsageTotals.Empty, surfaceUsage2.Agy, "Agy member defaults to empty");
        AssertEqual(450L, surfaceUsage2.Total.TotalTokens, "Total includes Codex + Work");

        // 2. Three-argument constructor
        var agy = new UsageTotals(300, 150, 60, 30);
        var surfaceUsage3 = new UsageBySurface(codex, work, agy);
        AssertEqual(900L, surfaceUsage3.Total.TotalTokens, "Total includes Codex + Work + Agy");

        // 3. Serialization and Deserialization of old JSON (without Agy)
        string legacyJson = JsonSerializer.Serialize(new { Codex = codex, Work = work });
        var deserializedLegacy = JsonSerializer.Deserialize<UsageBySurface>(legacyJson);
        Assert(deserializedLegacy is not null, "Legacy JSON should deserialize");
        AssertEqual(UsageTotals.Empty, deserializedLegacy!.Agy, "Legacy deserialization should default Agy to Empty");
        AssertEqual(450L, deserializedLegacy.Total.TotalTokens, "Legacy total must be Codex + Work");

        // 4. UsageSurface Enum
        AssertEqual(UsageSurface.Agy, Enum.Parse<UsageSurface>("Agy"), "UsageSurface must include Agy");
    }

    private static void TestSettings_LegacyQuotaInfoAlias()
    {
        var legacyMode = JsonSerializer.Deserialize<LeftClickDisplayMode>("\"QuotaInfo\"");
        AssertEqual(LeftClickDisplayMode.CodexQuota, legacyMode, "Legacy QuotaInfo must remain the Codex quota mode");

        var settings = new WidgetSettings();
        AssertEqual(LeftClickDisplayMode.Interaction, settings.LeftClickMode, "Fresh installs keep interaction as the default left-click mode");
        AssertEqual(UsageSource.Codex, settings.UsageSource, "Interaction mode defaults its remembered data source to Codex");
        Assert(!settings.PinInfoPanel, "Fresh installs keep the quota panel unpinned");
    }

    private static void TestClaudeQuotaParsing()
    {
        var now = DateTimeOffset.Now;
        JsonElement Cache(object usage, DateTimeOffset at) => JsonSerializer.SerializeToElement(new { version = 2, samples = new[] { new { t = at.ToUnixTimeMilliseconds(), org = "fixture", u = usage } } });
        var (limits, _) = ClaudeUsageReader.ParseQuotaHistory(Cache(new { fh = 100, sd = 28 }, now), now);
        AssertEqual(100d, limits!.Primary!.UsedPercent, "Exhausted five-hour quota is preserved");
        AssertEqual(28d, limits.Secondary!.UsedPercent, "Weekly utilization is a percentage");
        Assert(limits.Primary.ResetsAt is null, "Reset times are not guessed from history");
        (limits, _) = ClaudeUsageReader.ParseQuotaHistory(Cache(new { fh = 0 }, now), now);
        AssertEqual(0d, limits!.Primary!.UsedPercent, "A real zero remains available data");
        Assert(limits.Secondary is null, "Missing weekly quota is unknown, not exhausted");
        (limits, _) = ClaudeUsageReader.ParseQuotaHistory(Cache(new { fh = 10 }, now.AddMinutes(-10)), now);
        Assert(limits!.IsStale, "Old cache is marked");
        (limits, _) = ClaudeUsageReader.ParseQuotaHistory(Cache(new { fh = 10 }, now.AddMinutes(-31)), now);
        Assert(limits is null, "Expired quota is not shown");
        (limits, var warning) = ClaudeUsageReader.ParseQuotaHistory(Cache(new { }, now), now);
        Assert(limits is null && warning!.Contains("未提供"), "An empty Free account response is explicitly unavailable");
    }

    private static void TestClaudeSourceCycle(string rootDir)
    {
        AssertEqual(UsageSource.Agy, MainWindow.NextUsageSource(UsageSource.Codex), "Codex toggles to AGY");
        AssertEqual(UsageSource.Claude, MainWindow.NextUsageSource(UsageSource.Agy), "AGY toggles to Claude");
        AssertEqual(UsageSource.Codex, MainWindow.NextUsageSource(UsageSource.Claude), "Claude toggles to Codex");
        var path = Path.Combine(rootDir, "legacy-source.json");
        File.WriteAllText(path, """{"UsageSource":"Doubao","LeftClickMode":"DoubaoQuota","SettingsSchemaVersion":5,"Scale":1.2}""");
        var migrated = WidgetSettings.Load(path);
        AssertEqual(UsageSource.Claude, migrated.UsageSource, "Old source migrates without resetting settings");
        AssertEqual(LeftClickDisplayMode.ClaudeQuota, migrated.LeftClickMode, "Old click mode migrates");
        AssertEqual(1.2, migrated.Scale, "Unrelated settings survive migration");
    }

    private static void TestClaudeTranscript(string rootDir)
    {
        var root = Path.Combine(rootDir, "claude");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl");
        var now = DateTimeOffset.Now;
        string Row(int seconds, string type, object message) => JsonSerializer.Serialize(new { type, timestamp = now.AddSeconds(seconds), sessionId = "test-session", uuid = Guid.NewGuid().ToString(), effort = "max", message });
        var tool = new { type = "tool_use", id = "tool1", name = "Bash", input = new { command = "must not display" } };
        object Message(object[] content, string stop, int output) => new { id = "msg1", model = "claude-opus-5-5", content, stop_reason = stop, usage = new { input_tokens = 100, cache_creation_input_tokens = 200, cache_read_input_tokens = 300, output_tokens = output } };
        File.WriteAllLines(path, new[] {
            Row(-20, "user", new { role = "user", content = "private prompt" }),
            Row(-10, "assistant", Message(new object[] { new { type = "thinking", thinking = "raw reasoning must never show" }, new { type = "text", text = "正在核对项目文件，随后更新界面。" }, tool }, "tool_use", 500)),
            Row(-10, "assistant", Message(new object[] { tool }, "tool_use", 500))
        });
        var reader = new ClaudeUsageReader(root, Path.Combine(root, "missing-quota.json"), () => now);
        var usage = reader.ReadSnapshot();
        AssertEqual(new UsageTotals(600, 500, 300, 0), usage.CurrentConversation!.Tokens, "Message blocks do not duplicate usage; cached input is included once");
        var activity = reader.Poll();
        AssertEqual("claude-opus-5-5", activity.Model, "Actual Claude model is shown");
        Assert(activity.TokensPerSecond is null, "Saved block timestamps cannot establish Claude streaming TPS");
        AssertEqual("opus 5 5 max · -- t/s", activity.ModelSpeedText, "Keep the actual model while speed is unavailable");
        AssertEqual("正在核对项目文件，随后更新界面。", activity.StatusText, "Public Chinese progress is shown");
        Assert(activity.WorkDetail!.Contains("运行命令"), "Tool status is separate");
        File.AppendAllLines(path, new[] {
            Row(-5, "user", new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "tool1", content = "private result" } } }),
            Row(-1, "assistant", new { id = "msg2", model = "claude-opus-5-5", stop_reason = "end_turn", content = new[] { new { type = "text", text = "已完成更新，检查通过。" } }, usage = new { input_tokens = 50, output_tokens = 100, cache_read_input_tokens = 30, cache_creation_input_tokens = 20 } })
        });
        var completed = reader.Poll();
        Assert(!completed.IsWorking && completed.CompletionRevision > activity.CompletionRevision, "End-turn ends the task");
        AssertEqual("已完成更新，检查通过。", completed.CompletionText, "Final text is retained");
        AssertEqual(600L, reader.ReadSnapshot().Today.Claude.OutputTokens, "Output totals include both distinct messages");
        AssertEqual(completed.CompletionRevision, reader.Poll().CompletionRevision, "Completion is not repeated on polling");
    }

    private static void TestSettings_CorruptFileBackup(string rootDir)
    {
        var settingsDir = Path.Combine(rootDir, "settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, "{ this is not valid json");

        var settings = WidgetSettings.Load(settingsPath);
        var backups = Directory.GetFiles(settingsDir, "settings.json.corrupt-*.bak");

        AssertEqual(LeftClickDisplayMode.Interaction, settings.LeftClickMode, "Corrupt settings should recover with safe defaults");
        AssertEqual(1, backups.Length, "Corrupt settings should be backed up exactly once");
        AssertEqual("{ this is not valid json", File.ReadAllText(backups[0]), "Backup must preserve the original corrupt content");
    }

    private static void TestPlacement_FacesInward()
    {
        AssertEqual(-1d, WidgetPlacement.GetFacingScaleX(499, 0, 1000), "The left half must face right toward the screen center");
        AssertEqual(1d, WidgetPlacement.GetFacingScaleX(500, 0, 1000), "The midpoint and right half must face left toward the screen center");
        AssertEqual(1d, WidgetPlacement.GetFacingScaleX(3000, 1920, 1920), "A secondary screen work area must use its own midpoint");
        AssertEqual(1d, WidgetPlacement.GetFacingScaleX(double.NaN, 0, 1000), "Invalid coordinates must preserve the original orientation");
    }
}
