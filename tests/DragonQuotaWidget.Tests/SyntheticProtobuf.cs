using System.IO;

namespace DragonQuotaWidget.Tests;

public static class SyntheticProtobuf
{
    public static byte[] EncodeVarint(ulong value)
    {
        using var ms = new MemoryStream();
        while (value >= 0x80)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)(value & 0x7F));
        return ms.ToArray();
    }

    public static byte[] EncodeTag(int fieldNumber, int wireType)
    {
        return EncodeVarint((ulong)((fieldNumber << 3) | wireType));
    }

    public static byte[] WrapLengthDelimitedField(int fieldNumber, byte[] payload)
    {
        using var ms = new MemoryStream();
        var tag = EncodeTag(fieldNumber, 2);
        ms.Write(tag, 0, tag.Length);
        var length = EncodeVarint((ulong)payload.Length);
        ms.Write(length, 0, length.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    public static byte[] EncodeTimestamp(DateTimeOffset timestamp)
    {
        using var ms = new MemoryStream();
        long seconds = timestamp.ToUnixTimeSeconds();
        int nanos = (int)((timestamp.Ticks % TimeSpan.TicksPerSecond) * 100);

        var tag1 = EncodeTag(1, 0);
        ms.Write(tag1, 0, tag1.Length);
        var val1 = EncodeVarint((ulong)seconds);
        ms.Write(val1, 0, val1.Length);

        if (nanos > 0)
        {
            var tag2 = EncodeTag(2, 0);
            ms.Write(tag2, 0, tag2.Length);
            var val2 = EncodeVarint((ulong)nanos);
            ms.Write(val2, 0, val2.Length);
        }

        return ms.ToArray();
    }

    public static byte[] EncodeGenerationMetadata(
        long nonCachedInput,
        long totalOutput,
        long cachedInput = 0,
        long visibleOutput = 0,
        long reasoningOutput = 0)
    {
        using var ms = new MemoryStream();

        var tag2 = EncodeTag(2, 0);
        ms.Write(tag2, 0, tag2.Length);
        var val2 = EncodeVarint((ulong)nonCachedInput);
        ms.Write(val2, 0, val2.Length);

        var tag3 = EncodeTag(3, 0);
        ms.Write(tag3, 0, tag3.Length);
        var val3 = EncodeVarint((ulong)totalOutput);
        ms.Write(val3, 0, val3.Length);

        if (cachedInput > 0)
        {
            var tag5 = EncodeTag(5, 0);
            ms.Write(tag5, 0, tag5.Length);
            var val5 = EncodeVarint((ulong)cachedInput);
            ms.Write(val5, 0, val5.Length);
        }

        if (visibleOutput > 0)
        {
            var tag9 = EncodeTag(9, 0);
            ms.Write(tag9, 0, tag9.Length);
            var val9 = EncodeVarint((ulong)visibleOutput);
            ms.Write(val9, 0, val9.Length);
        }

        if (reasoningOutput > 0)
        {
            var tag10 = EncodeTag(10, 0);
            ms.Write(tag10, 0, tag10.Length);
            var val10 = EncodeVarint((ulong)reasoningOutput);
            ms.Write(val10, 0, val10.Length);
        }

        return ms.ToArray();
    }

    public static byte[] CreateStepMetadata(
        DateTimeOffset timestamp,
        long nonCachedInput,
        long totalOutput,
        long cachedInput = 0,
        long visibleOutput = 0,
        long reasoningOutput = 0,
        bool includeGeneration = true)
    {
        using var ms = new MemoryStream();

        var tsBytes = EncodeTimestamp(timestamp);
        var tag1 = EncodeTag(1, 2);
        ms.Write(tag1, 0, tag1.Length);
        var len1 = EncodeVarint((ulong)tsBytes.Length);
        ms.Write(len1, 0, len1.Length);
        ms.Write(tsBytes, 0, tsBytes.Length);

        if (includeGeneration)
        {
            var genBytes = EncodeGenerationMetadata(nonCachedInput, totalOutput, cachedInput, visibleOutput, reasoningOutput);
            var tag9 = EncodeTag(9, 2);
            ms.Write(tag9, 0, tag9.Length);
            var len9 = EncodeVarint((ulong)genBytes.Length);
            ms.Write(len9, 0, len9.Length);
            ms.Write(genBytes, 0, genBytes.Length);
        }

        return ms.ToArray();
    }
}
