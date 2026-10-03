using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>The client's answer to a one-field mutation confirmation form.</summary>
internal enum CalendarConfirmationAnswer
{
    Confirmed,
    Declined,

    /// <summary>The client accepted the form without a boolean <c>confirm</c> value.</summary>
    IncompleteAccept,

    Malformed
}

/// <summary>Builds the confirmation form shared by every confirmed mutation and classifies its answer.</summary>
/// <remarks>
/// The <c>confirm</c> field has no schema default. The SDK fills a missing field from its default on both
/// the server (classic <c>elicitation/create</c>) and the client (MRTR) side, so a default of
/// <see langword="false"/> would turn a client that accepts without filling the form into a decline the
/// user never gave. Without a default that answer stays incomplete and fails as
/// <c>confirmation_mismatch</c>.
/// </remarks>
internal static class CalendarMutationConfirmation
{
    internal const string FieldName = "confirm";

    internal const string IncompleteAcceptMessage =
        "The client accepted the confirmation without sending confirm=true, so nothing was changed. "
        + "The confirmation form needs a boolean confirm value; a client that accepts without filling in the "
        + "form cannot confirm this mutation.";

    internal static ElicitRequestParams.RequestSchema CreateSchema(string title, string description) => new()
    {
        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
        {
            [FieldName] = new ElicitRequestParams.BooleanSchema
            {
                Title = title,
                Description = description
            }
        },
        Required = [FieldName]
    };

    internal static CalendarConfirmationAnswer Read(InputResponse response) =>
        Read(response.Deserialize(InputResponse.ElicitResultJsonTypeInfo));

    internal static CalendarConfirmationAnswer Read(ElicitResult? elicitation)
    {
        if (elicitation?.Action is "decline" or "cancel")
            return CalendarConfirmationAnswer.Declined;
        if (!string.Equals(elicitation?.Action, "accept", StringComparison.Ordinal))
            return CalendarConfirmationAnswer.Malformed;
        if (elicitation!.Content is null
            || !elicitation.Content.TryGetValue(FieldName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CalendarConfirmationAnswer.IncompleteAccept;
        if (elicitation.Content.Count != 1)
            return CalendarConfirmationAnswer.Malformed;
        return value.ValueKind == JsonValueKind.True
            ? CalendarConfirmationAnswer.Confirmed
            : CalendarConfirmationAnswer.Declined;
    }
}
