using System.Security.Cryptography;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Retains immutable native sync state behind short, unguessable handles within one server session.</summary>
internal sealed class CalendarSyncCheckpointProtector
{
    internal const int MaximumCheckpointCharacters = 36;
    internal const int MaximumCheckpoints = 1024;
    internal const long MaximumRetainedBytes = 8L * 1024 * 1024;
    private const string HandlePrefix = "cs1_";
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, CheckpointEntry> _checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<CalendarSyncCheckpoint, CheckpointEntry> _states = [];
    private readonly LinkedList<string> _recency = new();
    private readonly object _gate = new();
    private long _retainedBytes;

    internal string ConfigurationBinding(CalDavOptions options)
    {
        // Keep the credential/configuration binding keyed with session-only
        // material. Neither credentials nor this binding appear in a handle.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        hmac.AppendData("caldav-sync-configuration-v1"u8);
        hmac.AppendData(JsonSerializer.SerializeToUtf8Bytes(options));
        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    internal string Protect(CalendarSyncCheckpoint checkpoint)
    {
        if (!IsValidState(checkpoint))
            throw InvalidCheckpoint();
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint).Length;
        if (serializedBytes > MaximumRetainedBytes)
            throw new CalendarProtocolException("payload_too_large", "The server's native sync state exceeded the checkpoint storage limit. No checkpoint was advanced.");
        lock (_gate)
        {
            if (_states.TryGetValue(checkpoint, out var existing))
            {
                Touch(existing);
                return existing.Handle;
            }
            MakeRoom(serializedBytes);
            return Retain(checkpoint, serializedBytes);
        }
    }

    internal CalendarSyncCheckpoint Unprotect(string checkpoint, string configurationBinding)
    {
        if (checkpoint is null || checkpoint.Length != MaximumCheckpointCharacters
            || !checkpoint.StartsWith(HandlePrefix, StringComparison.Ordinal))
            throw InvalidCheckpoint();
        lock (_gate)
        {
            if (!_checkpoints.TryGetValue(checkpoint, out var entry)
                || !string.Equals(entry.State.ConfigurationBinding, configurationBinding, StringComparison.Ordinal))
                throw InvalidCheckpoint();
            Touch(entry);
            return entry.State;
        }
    }

    internal int RetainedCheckpointCount
    {
        get
        {
            lock (_gate)
                return _checkpoints.Count;
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
                return _retainedBytes;
        }
    }

    private string Retain(CalendarSyncCheckpoint checkpoint, int serializedBytes)
    {
        string handle;
        do
        {
            handle = HandlePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }
        while (_checkpoints.ContainsKey(handle));
        var entry = new CheckpointEntry(checkpoint, handle, serializedBytes, _recency.AddFirst(handle));
        _checkpoints.Add(handle, entry);
        _states.Add(checkpoint, entry);
        _retainedBytes += serializedBytes;
        return handle;
    }

    private void MakeRoom(int serializedBytes)
    {
        while (_checkpoints.Count >= MaximumCheckpoints || _retainedBytes + serializedBytes > MaximumRetainedBytes)
        {
            var oldest = _checkpoints[_recency.Last!.Value];
            _recency.RemoveLast();
            _checkpoints.Remove(oldest.Handle);
            _states.Remove(oldest.State);
            _retainedBytes -= oldest.SerializedBytes;
        }
    }

    private void Touch(CheckpointEntry entry)
    {
        _recency.Remove(entry.Recency);
        _recency.AddFirst(entry.Recency);
    }

    private static bool IsValidState(CalendarSyncCheckpoint state) => state.Version == 1
        && !string.IsNullOrEmpty(state.CalendarHref) && !string.IsNullOrEmpty(state.SyncToken)
        && !string.IsNullOrEmpty(state.ConfigurationBinding);

    private static CalendarProtocolException InvalidCheckpoint() => new(
        "sync_reset_required",
        "The checkpoint is invalid, belongs to another session or configuration, or was evicted from bounded retention. If it was copied incorrectly, resend the exact original value; otherwise start again with calendarHref and no checkpoint to rebuild the inventory.");

    private sealed record CheckpointEntry(
        CalendarSyncCheckpoint State,
        string Handle,
        int SerializedBytes,
        LinkedListNode<string> Recency);
}

internal sealed record CalendarSyncCheckpoint(
    string CalendarHref,
    string SyncToken,
    bool Initial,
    string ConfigurationBinding,
    int Version = 1,
    bool OmitLimit = false);
