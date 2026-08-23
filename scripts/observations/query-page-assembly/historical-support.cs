using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class PageAssemblyObservationSupport
{
    internal const int CorpusCount = 201;
    internal const int Warmups = 12;
    internal const int Samples = 9;

    internal static CalendarResourceSnapshot Snapshot(int index, CalendarResourceProjectionKind kind)
    {
        var component = kind == CalendarResourceProjectionKind.Todo ? "VTODO" : "VEVENT";
        var componentFields = kind == CalendarResourceProjectionKind.Todo
            ? "DTSTAMP:20251231T000000Z\r\nDTSTART:20260101T000000Z\r\nDUE:20260101T010000Z\r\nSTATUS:NEEDS-ACTION\r\n"
            : "DTSTAMP:20251231T000000Z\r\nDTSTART:20260101T000000Z\r\nDTEND:20260101T010000Z\r\n";
        var body = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//EN\r\nBEGIN:{component}\r\nUID:item-{index:D3}\r\nSUMMARY:Item {index:D3}\r\n{componentFields}END:{component}\r\nEND:VCALENDAR\r\n";
        return CalendarResourceSnapshotFactory.Create(
            "https://calendar.example.test/calendars/team/",
            $"https://calendar.example.test/calendars/team/item-{index:D3}.ics",
            $"\"revision-{index:D3}\"",
            Encoding.UTF8.GetBytes(body));
    }

    internal static Observation Measure(
        string family,
        string revision,
        int pageSize,
        string[] corpusItems,
        Func<CallToolResult> create)
    {
        var firstWarmup = Validate(create(), pageSize, expectedCount: null);
        var admittedCount = firstWarmup.Items.Length;
        for (var index = 1; index < Warmups; index++)
            Validate(create(), pageSize, admittedCount);

        var allocatedSamples = new long[Samples];
        var elapsedTickSamples = new long[Samples];
        string? itemDigest = null;
        string[]? admittedItems = null;
        var threadId = Environment.CurrentManagedThreadId;
        for (var index = 0; index < Samples; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var result = create();
            var completed = Stopwatch.GetTimestamp();
            if (Environment.CurrentManagedThreadId != threadId)
                throw new InvalidOperationException("Page assembly changed the measurement thread.");
            allocatedSamples[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            elapsedTickSamples[index] = completed - started;
            var validated = Validate(result, pageSize, admittedCount);
            itemDigest ??= validated.Digest;
            admittedItems ??= validated.Items;
            if (!string.Equals(itemDigest, validated.Digest, StringComparison.Ordinal)
                || !admittedItems.SequenceEqual(validated.Items, StringComparer.Ordinal))
                throw new InvalidOperationException("The admitted item bytes or order changed between samples.");
        }

        var allocatedForMedian = allocatedSamples.ToArray();
        var elapsedTicksForMedian = elapsedTickSamples.ToArray();
        Array.Sort(allocatedForMedian);
        Array.Sort(elapsedTicksForMedian);
        return new Observation(
            family,
            revision,
            "historical-private-create-page",
            pageSize,
            CorpusCount,
            Warmups,
            Samples,
            allocatedForMedian[Samples / 2],
            elapsedTicksForMedian[Samples / 2],
            allocatedSamples,
            elapsedTickSamples,
            Stopwatch.Frequency,
            threadId,
            itemDigest!,
            admittedItems!,
            EncodedByteCount(admittedItems!),
            Digest(corpusItems),
            corpusItems.Sum(item => Encoding.UTF8.GetByteCount(item)),
            corpusItems,
            admittedCount,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            System.Runtime.GCSettings.IsServerGC,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
    }

    internal static void Write(IReadOnlyList<Observation> observations) =>
        Console.Out.Write("PAGE_ASSEMBLY_OBSERVATION_JSON=" + JsonSerializer.Serialize(observations) + Environment.NewLine);

    internal static string[] SerializeItems<T>(IEnumerable<T> items) =>
        items.Select(item => JsonSerializer.Serialize(item)).ToArray();

    private static ValidatedPage Validate(CallToolResult result, int pageSize, int? expectedCount)
    {
        if (result.IsError is true || result.StructuredContent is null)
            throw new InvalidOperationException("Page assembly did not return a successful structured result.");
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(result.StructuredContent));
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length is < 1 or > CorpusCount || items.Length > pageSize
            || expectedCount is not null && items.Length != expectedCount.Value)
            throw new InvalidOperationException($"Unexpected admitted item count: {items.Length}.");
        var pagination = document.RootElement.GetProperty("pagination");
        if (pagination.GetProperty("nextCursor").ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(pagination.GetProperty("nextCursor").GetString()))
            throw new InvalidOperationException("The 201-item corpus must produce a continuation cursor.");
        var itemText = items.Select(item => item.GetRawText()).ToArray();
        var bytes = new List<byte>();
        foreach (var item in itemText)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(item));
            bytes.Add((byte)'\n');
        }
        return new ValidatedPage(Digest(itemText), itemText);
    }

    private static string Digest(IEnumerable<string> items)
    {
        var bytes = new List<byte>();
        foreach (var item in items)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(item));
            bytes.Add((byte)'\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static int EncodedByteCount(IEnumerable<string> items) =>
        items.Sum(item => Encoding.UTF8.GetByteCount(item));

    internal sealed record Observation(
        string Family,
        string Revision,
        string Implementation,
        int PageSize,
        int CorpusCount,
        int Warmups,
        int Samples,
        long MedianAllocatedBytes,
        long MedianElapsedTicks,
        long[] AllocatedByteSamples,
        long[] ElapsedTickSamples,
        long StopwatchFrequency,
        int MeasurementThreadId,
        string AdmittedItemBytesSha256,
        string[] AdmittedItems,
        int AdmittedItemEncodedByteCount,
        string CorpusItemBytesSha256,
        int CorpusEncodedByteCount,
        string[] CorpusItems,
        int AdmittedItemCount,
        string Runtime,
        string RuntimeIdentifier,
        string OperatingSystem,
        string OperatingSystemArchitecture,
        bool IsServerGc,
        string ProcessArchitecture);

    private sealed record ValidatedPage(string Digest, string[] Items);
}
