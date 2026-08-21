using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DotnetAgents.CalDav.IntegrationTests.Fixtures;

internal sealed class OtlpLoopbackReceiver : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<OtlpRequest> _requests = new();
    private readonly SemaphoreSlim _requestSignal = new(0);
    private readonly TaskCompletionSource _shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _respond;
    private readonly Task _pump;

    private OtlpLoopbackReceiver(int port, bool respond)
    {
        _respond = respond;
        Endpoint = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
        _listener.Prefixes.Add($"{Endpoint.GetLeftPart(UriPartial.Authority)}/");
        _listener.Start();
        _pump = PumpAsync();
    }

    internal Uri Endpoint { get; }

    internal IReadOnlyList<OtlpRequest> Requests => _requests.ToArray();

    internal static OtlpLoopbackReceiver Start(bool respond = true)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return new OtlpLoopbackReceiver(port, respond);
    }

    internal Task<bool> WaitForRequestAsync(CancellationToken cancellationToken) =>
        _requestSignal.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

    internal async Task WaitForPathsAsync(
        IReadOnlyCollection<string> requiredPaths,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
        while (!requiredPaths.All(path => _requests.Any(request => request.Path == path)))
        {
            var remaining = deadline - TimeProvider.System.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Timed out waiting for OTLP paths: {string.Join(", ", requiredPaths)}");
            await _requestSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdownSignal.TrySetResult();
        _listener.Stop();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            _shutdown.IsCancellationRequested
            && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            System.Diagnostics.Debug.Assert(_shutdown.IsCancellationRequested);
        }
        _listener.Close();
        _requestSignal.Dispose();
        _shutdown.Dispose();
    }

    private async Task PumpAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync()
                .WaitAsync(_shutdown.Token)
                .ConfigureAwait(false);
            await CaptureAsync(context, _shutdown.Token).ConfigureAwait(false);
        }
    }

    private async Task CaptureAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(new OtlpRequest(context.Request.Url?.AbsolutePath ?? string.Empty, body.ToArray()));
        _requestSignal.Release();
        if (!_respond)
        {
            await _shutdownSignal.Task.ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentLength64 = 0;
        context.Response.Close();
    }
}

internal sealed record OtlpRequest(string Path, byte[] Body);

internal static class OtlpProtobufReader
{
    internal static IReadOnlyList<OtlpSpan> ReadSpans(IEnumerable<OtlpRequest> requests) => requests
        .Where(request => request.Path == "/v1/traces")
        .SelectMany(request => ReadTraceRequest(request.Body))
        .ToArray();

    internal static IReadOnlyList<OtlpLogRecord> ReadLogs(IEnumerable<OtlpRequest> requests) => requests
        .Where(request => request.Path == "/v1/logs")
        .SelectMany(request => ReadLogRequest(request.Body))
        .ToArray();

    internal static IReadOnlyList<OtlpMetric> ReadMetrics(IEnumerable<OtlpRequest> requests) => requests
        .Where(request => request.Path == "/v1/metrics")
        .SelectMany(request => ReadMetricRequest(request.Body))
        .ToArray();

    internal static bool ContainsUtf8(IEnumerable<OtlpRequest> requests, string path, string value)
    {
        var needle = Encoding.UTF8.GetBytes(value);
        return requests.Where(request => request.Path == path)
            .Any(request => request.Body.AsSpan().IndexOf(needle) >= 0);
    }

    internal static bool ContainsUtf8(IEnumerable<OtlpRequest> requests, string value)
    {
        var needle = Encoding.UTF8.GetBytes(value);
        return requests.Any(request => request.Body.AsSpan().IndexOf(needle) >= 0);
    }

    private static IEnumerable<OtlpSpan> ReadTraceRequest(byte[] payload)
    {
        foreach (var resourceSpans in MessageFields(payload, 1))
        foreach (var scopeSpans in MessageFields(resourceSpans, 2))
        {
            var scopeName = ReadScopeName(scopeSpans);
            foreach (var span in MessageFields(scopeSpans, 2))
                yield return ReadSpan(scopeName, span);
        }
    }

    private static IEnumerable<OtlpLogRecord> ReadLogRequest(byte[] payload)
    {
        foreach (var resourceLogs in MessageFields(payload, 1))
        foreach (var scopeLogs in MessageFields(resourceLogs, 2))
        {
            var scopeName = ReadScopeName(scopeLogs);
            foreach (var record in MessageFields(scopeLogs, 2))
                yield return ReadLogRecord(scopeName, record);
        }
    }

    private static IEnumerable<OtlpMetric> ReadMetricRequest(byte[] payload)
    {
        foreach (var resourceMetrics in MessageFields(payload, 1))
        foreach (var scopeMetrics in MessageFields(resourceMetrics, 2))
        {
            var scopeName = ReadScopeName(scopeMetrics);
            foreach (var metric in MessageFields(scopeMetrics, 2))
            {
                var fields = ReadFields(metric);
                yield return new OtlpMetric(
                    scopeName,
                    ReadString(fields, 1),
                    ReadString(fields, 3),
                    ReadMetricDataPointAttributes(fields));
            }
        }
    }

    private static OtlpSpan ReadSpan(string scopeName, byte[] payload)
    {
        var fields = ReadFields(payload);
        return new OtlpSpan(
            scopeName,
            ReadString(fields, 5),
            ReadBytes(fields, 1),
            ReadBytes(fields, 2),
            ReadBytes(fields, 4),
            ReadAttributes(fields, 9),
            ReadString(fields, 3),
            fields.Count(field => field.Number == 11));
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadMetricDataPointAttributes(
        IReadOnlyList<ProtoField> metricFields)
    {
        var attributes = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var data in metricFields.Where(field => field.Number is 5 or 7 or 9 or 10 or 11
                     && field.Bytes is not null))
        {
            var attributeFieldNumber = data.Number switch
            {
                5 or 7 or 11 => 7,
                9 => 9,
                10 => 1,
                _ => throw new InvalidDataException("Unsupported OTLP metric data kind.")
            };
            foreach (var point in MessageFields(data.Bytes!, 1))
                attributes.Add(ReadAttributes(ReadFields(point), attributeFieldNumber));
        }
        return attributes;
    }

    private static OtlpLogRecord ReadLogRecord(string scopeName, byte[] payload)
    {
        var fields = ReadFields(payload);
        return new OtlpLogRecord(
            scopeName,
            ReadAnyValue(fields.FirstOrDefault(field => field.Number == 5)?.Bytes),
            ReadBytes(fields, 9),
            ReadBytes(fields, 10),
            ReadAttributes(fields, 6));
    }

    private static string ReadScopeName(byte[] scopePayload)
    {
        var scope = MessageFields(scopePayload, 1).FirstOrDefault();
        return scope is null ? string.Empty : ReadString(ReadFields(scope), 1);
    }

    private static IReadOnlyDictionary<string, object?> ReadAttributes(
        IReadOnlyList<ProtoField> fields,
        int fieldNumber) => fields
        .Where(field => field.Number == fieldNumber && field.Bytes is not null)
        .Select(ReadKeyValue)
        .Where(item => item.Key.Length > 0)
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static KeyValuePair<string, object?> ReadKeyValue(ProtoField field)
    {
        var fields = ReadFields(field.Bytes!);
        var key = ReadString(fields, 1);
        var value = ReadAnyValue(fields.FirstOrDefault(candidate => candidate.Number == 2)?.Bytes);
        return new KeyValuePair<string, object?>(key, value);
    }

    private static object? ReadAnyValue(byte[]? payload)
    {
        if (payload is null)
            return null;
        var field = ReadFields(payload).FirstOrDefault();
        if (field is null)
            return null;
        return field.Number switch
        {
            1 => field.Bytes is null ? null : Encoding.UTF8.GetString(field.Bytes),
            2 => field.Integer != 0,
            3 => unchecked((long)field.Integer),
            _ => null
        };
    }

    private static IEnumerable<byte[]> MessageFields(byte[] payload, int fieldNumber) =>
        ReadFields(payload)
            .Where(field => field.Number == fieldNumber && field.Bytes is not null)
            .Select(field => field.Bytes!);

    private static string ReadString(IReadOnlyList<ProtoField> fields, int fieldNumber)
    {
        var bytes = fields.FirstOrDefault(field => field.Number == fieldNumber)?.Bytes;
        return bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadBytes(IReadOnlyList<ProtoField> fields, int fieldNumber) =>
        fields.FirstOrDefault(field => field.Number == fieldNumber)?.Bytes ?? [];

    private static IReadOnlyList<ProtoField> ReadFields(byte[] payload)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var number = checked((int)(key >> 3));
            var wireType = checked((int)(key & 7));
            fields.Add(ReadField(payload, ref offset, number, wireType));
        }
        return fields;
    }

    private static ProtoField ReadField(byte[] payload, ref int offset, int number, int wireType) => wireType switch
    {
        0 => new ProtoField(number, ReadVarint(payload, ref offset), null),
        1 => new ProtoField(number, 0, ReadFixed(payload, ref offset, 8)),
        2 => new ProtoField(number, 0, ReadLengthDelimited(payload, ref offset)),
        5 => new ProtoField(number, 0, ReadFixed(payload, ref offset, 4)),
        _ => throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.")
    };

    private static byte[] ReadLengthDelimited(byte[] payload, ref int offset)
    {
        var length = checked((int)ReadVarint(payload, ref offset));
        return ReadFixed(payload, ref offset, length);
    }

    private static byte[] ReadFixed(byte[] payload, ref int offset, int length)
    {
        if (length < 0 || offset > payload.Length - length)
            throw new InvalidDataException("Truncated protobuf field.");
        var value = payload.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    private static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= payload.Length)
                throw new InvalidDataException("Truncated protobuf varint.");
            var current = payload[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Oversized protobuf varint.");
    }

    private sealed record ProtoField(int Number, ulong Integer, byte[]? Bytes);
}

internal sealed record OtlpSpan(
    string ScopeName,
    string Name,
    byte[] TraceId,
    byte[] SpanId,
    byte[] ParentSpanId,
    IReadOnlyDictionary<string, object?> Attributes,
    string TraceState,
    int EventCount);

internal sealed record OtlpLogRecord(
    string ScopeName,
    object? Body,
    byte[] TraceId,
    byte[] SpanId,
    IReadOnlyDictionary<string, object?> Attributes);

internal sealed record OtlpMetric(
    string ScopeName,
    string Name,
    string Unit,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> DataPointAttributes);
