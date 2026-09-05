using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal static partial class CalendarMetadataPatchProtocol
{
    internal static void Validate(CalendarMetadataPatch patch)
    {
        if (patch is null || patch.DisplayName is null && patch.Description is null)
            throw InvalidInput();
        ValidateProperty(patch.DisplayName, displayName: true);
        ValidateProperty(patch.Description, displayName: false);
    }

    private static void ValidateProperty(CalendarMetadataTextPatch? property, bool displayName)
    {
        if (property is null)
            return;
        if (property.Operation == "remove")
        {
            if (property.Value is not null || property.Language is not null)
                throw InvalidInput();
            return;
        }
        if (property.Operation != "set" || property.Value is null)
            throw InvalidInput();
        ValidateValue(property, displayName);
    }

    private static void ValidateValue(CalendarMetadataTextPatch property, bool displayName)
    {
        var value = property.Value!;
        if (value.Length > (displayName ? 256 : 4096)
            || displayName && string.IsNullOrWhiteSpace(value)
            || displayName && property.Language is not null)
            throw InvalidInput();
        if (property.Language is { } language && (language.Length > 64 || !LanguagePattern().IsMatch(language)))
            throw InvalidInput();
        try
        {
            XmlConvert.VerifyXmlChars(value);
        }
        catch (XmlException)
        {
            throw InvalidInput();
        }
    }

    internal static string Body(CalendarMetadataPatch patch) => new XElement(CalendarMetadataProtocol.Dav + "propertyupdate",
        Changes(patch).Select(change => Instruction(change.Key, change.Value)))
        .ToString(SaveOptions.DisableFormatting);

    private static XElement Instruction(XName name, CalendarMetadataTextPatch patch)
    {
        var property = new XElement(name);
        if (patch.Operation == "set")
        {
            property.Value = patch.Value!;
            if (patch.Language is not null)
                property.Add(new XAttribute(XNamespace.Xml + "lang", patch.Language));
        }
        return new XElement(CalendarMetadataProtocol.Dav + (patch.Operation == "set" ? "set" : "remove"),
            new XElement(CalendarMetadataProtocol.Dav + "prop", property));
    }

    internal static CalendarMetadataPatchDispatch ReadDispatch(
        string href,
        CalendarMetadataPatch patch,
        CalendarProtocolResponse response)
    {
        if (response.StatusCode != 207)
            return HttpDispatch(response.StatusCode);
        try
        {
            var properties = CalendarMetadataProtocol.ReadProperties(href, response.Body);
            return PropertyDispatch(patch, properties);
        }
        catch (Exception exception) when (exception is CalendarProtocolException or XmlException)
        {
            return Uncertain();
        }
    }

    private static CalendarMetadataPatchDispatch PropertyDispatch(
        CalendarMetadataPatch patch,
        IReadOnlyDictionary<XName, CalendarMetadataProperty> properties)
    {
        var requested = Changes(patch).Select(change => change.Key).ToArray();
        if (properties.Count != requested.Length || requested.Any(name => !properties.ContainsKey(name)))
            return Uncertain();
        var statuses = requested.Select(name => properties[name].StatusCode).ToArray();
        if (statuses.All(status => status is 200 or 201 or 204))
            return new(CalendarMutationState.Committed, null);
        if (statuses.All(status => status >= 400))
        {
            var status = statuses.FirstOrDefault(value => value != 424, 424);
            return new(CalendarMutationState.NotCommitted, CalendarMetadataProtocol.StatusFailure(status));
        }
        return Uncertain();
    }

    private static CalendarMetadataPatchDispatch HttpDispatch(int status) => status switch
    {
        >= 400 and < 500 => new(CalendarMutationState.NotCommitted, CalendarMetadataProtocol.StatusFailure(status)),
        501 => new(CalendarMutationState.NotCommitted, CalendarMetadataProtocol.StatusFailure(status)),
        _ => Uncertain()
    };

    internal static bool Matches(CalendarMetadataPatch patch, CalendarMetadataObservation observed) =>
        Changes(patch).All(change => MatchesProperty(change.Value, observed.Properties.GetValueOrDefault(change.Key)));

    private static bool MatchesProperty(CalendarMetadataTextPatch expected, CalendarMetadataProperty? observed)
    {
        if (observed is null)
            return false;
        if (expected.Operation == "remove")
            return observed.StatusCode == 404;
        if (observed.StatusCode != 200 || observed.Element.Elements().Any() || observed.Element.Value != expected.Value)
            return false;
        return string.Equals(CalendarMetadataProtocol.Language(observed.Element), expected.Language, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<KeyValuePair<XName, CalendarMetadataTextPatch>> Changes(CalendarMetadataPatch patch)
    {
        if (patch.DisplayName is not null)
            yield return new(CalendarMetadataProtocol.Dav + "displayname", patch.DisplayName);
        if (patch.Description is not null)
            yield return new(CalendarMetadataProtocol.CalDav + "calendar-description", patch.Description);
    }

    internal static CalendarMetadataPatchDispatch Uncertain() => new(CalendarMutationState.Unknown,
        new CalendarProtocolException("indeterminate", "The property update may have committed. Inspect the Calendar before deciding another write."));

    private static CalendarProtocolException InvalidInput() => new("invalid_input",
        "Set or remove at least one Calendar property. Sets require a bounded value; only description accepts a language tag.");

    [GeneratedRegex("^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex LanguagePattern();
}

internal sealed record CalendarMetadataPatchDispatch(CalendarMutationState State, CalendarProtocolException? Error);
