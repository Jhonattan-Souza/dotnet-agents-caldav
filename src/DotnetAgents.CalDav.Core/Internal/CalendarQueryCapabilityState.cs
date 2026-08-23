using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Bounded process-lifetime evidence that Calendar multiget is verified unavailable.</summary>
internal sealed class CalendarQueryCapabilityState
{
    internal const int MaximumEntries = 256;
    private readonly ConcurrentDictionary<CalendarMultigetCapabilityKey, byte> _unavailable = new();
    private readonly object _gate = new();
    private readonly byte[] _contextKey = RandomNumberGenerator.GetBytes(32);
    private string? _activeContextDigest;
    private long _generation;

    internal CalendarMultigetCapabilityObservation ObserveContext(CalDavOptions options, string calendarHref)
    {
        var contextDigest = ContextDigest(options, _contextKey);
        lock (_gate)
        {
            if (_activeContextDigest is not null
                && !string.Equals(_activeContextDigest, contextDigest, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _generation);
                _unavailable.Clear();
            }
            _activeContextDigest = contextDigest;
            return new CalendarMultigetCapabilityObservation(
                CalendarMultigetCapabilityKey.Create(options, calendarHref, contextDigest),
                _generation);
        }
    }

    internal bool IsUnavailable(CalendarMultigetCapabilityObservation observation) =>
        observation.Generation == Volatile.Read(ref _generation)
        && _unavailable.ContainsKey(observation.Key);

    internal void ObserveUnavailable(CalendarMultigetCapabilityObservation observation)
    {
        lock (_gate)
        {
            if (observation.Generation != _generation
                || _unavailable.ContainsKey(observation.Key)
                || _unavailable.Count >= MaximumEntries)
                return;
            _unavailable.TryAdd(observation.Key, 0);
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _unavailable.Clear();
        }
    }

    internal int Count => _unavailable.Count;

    private static string ContextDigest(CalDavOptions options, byte[] contextKey)
    {
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(options.BaseUrl);
                writer.WriteStringValue(options.Username);
                writer.WriteStringValue(options.Password);
                writer.WriteStringValue(options.CalendarHrefs);
                writer.WriteStringValue(options.DefaultEventCalendarName);
                writer.WriteStringValue(options.DefaultTodoCalendarName);
                writer.WriteNumberValue(options.RequestTimeout.Ticks);
                writer.WriteEndArray();
            }
            buffer.TryGetBuffer(out var written);
            return Convert.ToHexString(HMACSHA256.HashData(
                contextKey,
                written.AsSpan(0, checked((int)buffer.Length))));
        }
        finally
        {
            if (buffer.TryGetBuffer(out var written))
                CryptographicOperations.ZeroMemory(written.AsSpan(0, checked((int)buffer.Length)));
        }
    }

    internal sealed record CalendarMultigetCapabilityKey(
        string Origin,
        string CalendarHref,
        string ContextDigest)
    {
        internal static CalendarMultigetCapabilityKey Create(
            CalDavOptions options,
            string calendarHref,
            string contextDigest)
        {
            var origin = new Uri(options.BaseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
            return new CalendarMultigetCapabilityKey(
                origin,
                calendarHref,
                contextDigest);
        }
    }
}

internal readonly record struct CalendarMultigetCapabilityObservation(
    CalendarQueryCapabilityState.CalendarMultigetCapabilityKey Key,
    long Generation);
