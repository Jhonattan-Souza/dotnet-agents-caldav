using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class QueryPageAssemblyObservation
{
    private const string Revision = "61f2607383807f96464f33350e608180c1abee49";
    private const int CorpusCount = 201;
    private const int Warmups = 12;
    private const int Samples = 9;

    private static int Main(string[] arguments)
    {
        if (arguments.Length != 1 || !Path.IsPathFullyQualified(arguments[0]))
            throw new ArgumentException("The absolute results directory containing historical observations is required.");
        var issuer = new CalendarQueryCursorIssuer(new CalendarQueryCursorKey(Options.Create(new CalDavOptions()), new byte[64]));
        var observations = new List<Observation>();
        foreach (var family in new[] { "entity", "occurrence", "todo" })
        {
            var entityCodec = family == "entity" ? new CalendarEntityQueryPageCodec(issuer) : null;
            var occurrenceCodec = family == "occurrence" ? new CalendarOccurrenceQueryPageCodec(issuer) : null;
            var todoCodec = family == "todo" ? new CalendarTodoQueryPageCodec(issuer) : null;
            var legacy = JsonSerializer.Deserialize<HistoricalObservation[]>(File.ReadAllText(Path.Combine(arguments[0], $"{family}-historical.json")))
                ?? throw new InvalidOperationException($"Historical {family} observations are unreadable.");
            foreach (var historical in legacy)
            {
                var items = historical.CorpusItems
                    .Select(item => new StoredCalendarEntityQueryItem(Encoding.UTF8.GetBytes(item)))
                    .ToImmutableArray();
                var snapshot = new CalendarQuerySnapshot(
                    Guid.Parse("cce617c9-0eb2-4d7f-bf35-7a43e6e7d40b"),
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    items,
                    "[]"u8.ToArray(),
                    items.Sum(item => (long)item.JsonByteCount));
                observations.Add(family switch
                {
                    "entity" => Measure(family, historical.PageSize, historical.AdmittedItemBytesSha256, historical.AdmittedItems, historical.CorpusItemBytesSha256, historical.CorpusEncodedByteCount,
                        () => entityCodec!.Admit(snapshot, 0, historical.PageSize, CancellationToken.None).Value!),
                    "occurrence" => Measure(family, historical.PageSize, historical.AdmittedItemBytesSha256, historical.AdmittedItems, historical.CorpusItemBytesSha256, historical.CorpusEncodedByteCount,
                        () => occurrenceCodec!.Admit(snapshot, 0, historical.PageSize, CancellationToken.None).Value!),
                    "todo" => Measure(family, historical.PageSize, historical.AdmittedItemBytesSha256, historical.AdmittedItems, historical.CorpusItemBytesSha256, historical.CorpusEncodedByteCount,
                        () => todoCodec!.Admit(snapshot, 0, historical.PageSize, CancellationToken.None).Value!),
                    _ => throw new ArgumentOutOfRangeException(nameof(family))
                });
            }
        }
        Console.Out.Write("PAGE_ASSEMBLY_OBSERVATION_JSON=" + JsonSerializer.Serialize(observations) + Environment.NewLine);
        return 0;
    }

    private static Observation Measure<T>(
        string family,
        int pageSize,
        string historicalDigest,
        string[] historicalItems,
        string corpusDigest,
        int corpusByteCount,
        Func<QueryPage<T>> create)
    {
        var expectedCurrentCount = ExpectedCurrentCount(family, pageSize, historicalItems.Length);
        for (var index = 0; index < Warmups; index++)
            Validate(create().StructuredContent, pageSize, expectedCurrentCount, historicalItems.Length);

        var allocatedSamples = new long[Samples];
        var elapsedTickSamples = new long[Samples];
        string? comparableDigest = null;
        string? outputDigest = null;
        string[]? admittedItems = null;
        int? admittedCount = null;
        int? encodedByteCount = null;
        var threadId = Environment.CurrentManagedThreadId;
        for (var index = 0; index < Samples; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var page = create();
            var completed = Stopwatch.GetTimestamp();
            if (Environment.CurrentManagedThreadId != threadId)
                throw new InvalidOperationException("Page assembly changed the measurement thread.");
            allocatedSamples[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            elapsedTickSamples[index] = completed - started;
            var validated = Validate(page.StructuredContent, pageSize, expectedCurrentCount, historicalItems.Length);
            comparableDigest ??= validated.ComparableDigest;
            outputDigest ??= validated.OutputDigest;
            admittedItems ??= validated.OutputItems;
            admittedCount ??= validated.AdmittedCount;
            encodedByteCount ??= validated.OutputEncodedByteCount;
            EnsureStable(
                comparableDigest,
                outputDigest,
                admittedItems,
                admittedCount.Value,
                encodedByteCount.Value,
                validated);
        }

        if (!string.Equals(comparableDigest, historicalDigest, StringComparison.Ordinal)
            || !historicalItems.SequenceEqual(admittedItems!.Take(historicalItems.Length), StringComparer.Ordinal))
            throw new InvalidOperationException("Current codec did not preserve the historical admitted item bytes and order.");

        var allocatedForMedian = allocatedSamples.ToArray();
        var elapsedTicksForMedian = elapsedTickSamples.ToArray();
        Array.Sort(allocatedForMedian);
        Array.Sort(elapsedTicksForMedian);
        return new Observation(
            family,
            Revision,
            "current-page-codec",
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
            outputDigest!,
            admittedItems!,
            admittedCount!.Value,
            encodedByteCount!.Value,
            comparableDigest!,
            historicalItems.Sum(item => Encoding.UTF8.GetByteCount(item)),
            corpusDigest,
            corpusByteCount,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            System.Runtime.GCSettings.IsServerGC,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
    }

    private static int ExpectedCurrentCount(string family, int pageSize, int historicalCount) =>
        family == "todo" && pageSize == 200 ? 200 : historicalCount;

    private static void EnsureStable(
        string? comparableDigest,
        string? outputDigest,
        IReadOnlyList<string>? outputItems,
        int admittedCount,
        int encodedByteCount,
        ValidatedPage current)
    {
        if (!string.Equals(comparableDigest, current.ComparableDigest, StringComparison.Ordinal)
            || !string.Equals(outputDigest, current.OutputDigest, StringComparison.Ordinal)
            || outputItems is null
            || !outputItems.SequenceEqual(current.OutputItems, StringComparer.Ordinal)
            || admittedCount != current.AdmittedCount
            || encodedByteCount != current.OutputEncodedByteCount)
            throw new InvalidOperationException("The admitted item bytes or order changed between samples.");
    }

    private static ValidatedPage Validate(
        JsonElement structuredContent,
        int pageSize,
        int expectedCurrentCount,
        int comparableCount)
    {
        var items = structuredContent.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length != expectedCurrentCount || items.Length is < 1 or > CorpusCount || items.Length > pageSize)
            throw new InvalidOperationException($"Unexpected admitted item count: {items.Length}.");
        var pagination = structuredContent.GetProperty("pagination");
        if (pagination.GetProperty("nextCursor").ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(pagination.GetProperty("nextCursor").GetString()))
            throw new InvalidOperationException("The 201-item corpus must produce a continuation cursor.");
        var outputItems = items.Select(item => item.GetRawText()).ToArray();
        return new ValidatedPage(
            Digest(outputItems),
            Digest(outputItems.Take(comparableCount)),
            outputItems,
            outputItems.Sum(item => Encoding.UTF8.GetByteCount(item)),
            items.Length);
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

    private sealed record Observation(
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
        int AdmittedItemCount,
        int AdmittedItemEncodedByteCount,
        string HistoricalComparablePrefixSha256,
        int HistoricalComparablePrefixEncodedByteCount,
        string SourceCorpusItemBytesSha256,
        int SourceCorpusEncodedByteCount,
        string Runtime,
        string RuntimeIdentifier,
        string OperatingSystem,
        string OperatingSystemArchitecture,
        bool IsServerGc,
        string ProcessArchitecture);

    private sealed record HistoricalObservation(
        int PageSize,
        string AdmittedItemBytesSha256,
        string[] AdmittedItems,
        string CorpusItemBytesSha256,
        int CorpusEncodedByteCount,
        string[] CorpusItems,
        int AdmittedItemCount);

    private sealed record ValidatedPage(
        string OutputDigest,
        string ComparableDigest,
        string[] OutputItems,
        int OutputEncodedByteCount,
        int AdmittedCount);
}
