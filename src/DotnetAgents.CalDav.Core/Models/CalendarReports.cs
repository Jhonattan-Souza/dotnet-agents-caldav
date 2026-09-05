using System.Text.Json.Serialization;

namespace DotnetAgents.CalDav.Core.Models;

/// <summary>One exact Calendar and a UTC interval with whole-second boundaries.</summary>
public sealed record CalendarFreeBusyRequest(string CalendarHref, DateTimeOffset From, DateTimeOffset To);

/// <summary>A server-computed free/busy period. Unrecognized busy types retain the server's value.</summary>
public sealed record CalendarBusyPeriod(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("busyType")] string BusyType);

/// <summary>Complete native free/busy information for the requested interval.</summary>
public sealed record CalendarFreeBusyResult(
    [property: JsonPropertyName("calendarHref")] string CalendarHref,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("periods")] IReadOnlyList<CalendarBusyPeriod> Periods)
{
    [JsonPropertyName("outcome")] public string Outcome => "success";
    [JsonPropertyName("temporalAuthority")] public string TemporalAuthority => "server";
    [JsonPropertyName("complete")] public bool Complete => true;
}

/// <summary>Starts an inventory or resumes an authenticated native synchronization checkpoint.</summary>
public abstract record CalendarResourceChangesRequest(int PageSize)
{
    public sealed record Start(string CalendarHref, int PageSize = 100) : CalendarResourceChangesRequest(PageSize);
    public sealed record Continue(string Checkpoint, int PageSize = 100) : CalendarResourceChangesRequest(PageSize);
}

/// <summary>A changed resource's observational Entity Tag, or removal from the caller's view.</summary>
public sealed record CalendarResourceChange(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("etag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Etag = null);

/// <summary>One complete native response page. A checkpoint advances only after the whole page validates.</summary>
public sealed record CalendarResourceChangesResult(
    [property: JsonPropertyName("calendarHref")] string CalendarHref,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("changes")] IReadOnlyList<CalendarResourceChange> Changes,
    [property: JsonPropertyName("checkpoint")] string Checkpoint,
    [property: JsonPropertyName("hasMore")] bool HasMore)
{
    [JsonPropertyName("outcome")] public string Outcome => "success";
    [JsonPropertyName("checkpointLifetime")] public string CheckpointLifetime => "session";
    [JsonPropertyName("removalMeaning")] public string RemovalMeaning => "removed_from_view";
}
