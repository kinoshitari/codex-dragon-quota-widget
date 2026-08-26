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
            RunTest("Protobuf: Basic tokens and cached/reasoning semantics", TestProtobufParsing_BasicTokensAndSemantics);
            RunTest("Protobuf: Reasoning clamping and field invariant", TestProtobufParsing_ReasoningClamping);
            RunTest("Protobuf: Non-token events without field 9 skipped", TestProtobufParsing_NonTokenEventSkipped);
            RunTest("Protobuf: Token events without timestamp skipped", TestProtobufParsing_MissingTimestampSkipped);
            RunTest("Protobuf: Malformed bytes and corrupt varint tolerance", TestProtobufParsing_MalformedTolerance);
            RunTest("Storage: Trajectory deduplication by ID picks newest last-write file", () => TestTrajectoryDeduplication_PicksNewestFile(testDir));
            RunTest("Storage: Period aggregation across Today, 24h, 7d, 30d, AllTime and surface isolation", () => TestPeriodAggregation_AllPeriodsAndSurfaces(testDir));
            RunTest("Storage: Current conversation resolved by latest event timestamp", () => TestCurrentConversation_PicksLatestByEventTimestamp(testDir));
            RunTest("Storage: Malformed and empty database tolerance without crashing", () => TestMalformedDatabaseTolerance(testDir));
            RunTest("Quota: Full JSON parsing for Gemini weekly (10080m) and 5h (300m) buckets", TestQuotaParsing_FullResponse);
            RunTest("Quota: Missing window behavior (do not infer absent windows)", TestQuotaParsing_MissingWindows);
            RunTest("Quota: Multiline stdout with preamble logs parsed correctly", TestQuotaParsing_MultilineStdoutWithPreamble);
            RunTest("Quota: Caching and process fan-out prevention", TestQuotaCommandRunner_Caching);
            RunTest("Models: Existing Codex/Work calculation and serialization compatibility", TestUsageModels_CodexCompatibility);
            RunTest("Settings: Legacy QuotaInfo mode maps to Codex quota", TestSettings_LegacyQuotaInfoAlias);

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

    private static void TestQuotaCommandRunner_Caching()
    {
        int invocationCount = 0;
        AntigravityUsageReader.CommandRunner mockRunner = (file, args, timeout) =>
        {
            invocationCount++;
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
            quotaCacheDuration: TimeSpan.FromSeconds(30));

        var snap1 = reader.ReadSnapshot();
        AssertEqual(1, invocationCount, "First read should invoke quota runner");
        Assert(snap1.RateLimits is not null, "RateLimits should be populated");

        // Second call 10 seconds later (within 30s cache)
        now = now.AddSeconds(10);
        var snap2 = reader.ReadSnapshot();
        AssertEqual(1, invocationCount, "Second read within cache duration must NOT re-invoke runner");
        Assert(snap2.RateLimits is not null, "RateLimits should be returned from cache");

        // Third call 35 seconds later (past cache duration)
        now = now.AddSeconds(35);
        var snap3 = reader.ReadSnapshot();
        AssertEqual(2, invocationCount, "Read after cache expiry must re-invoke runner");
    }

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
    }
}
