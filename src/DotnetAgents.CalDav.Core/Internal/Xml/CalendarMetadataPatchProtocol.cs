using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal static partial class CalendarMetadataPatchProtocol
{
    /// <summary>Validates the patch and returns its property instructions in request order.</summary>
    internal static IReadOnlyList<CalendarMetadataPropertyChange> Plan(CalendarMetadataPatch patch)
    {
        Validate(patch);
        return Changes(patch).ToArray();
    }

    private static void Validate(CalendarMetadataPatch patch)
    {
        if (patch is null || patch.DisplayName is null && patch.Description is null
            && patch.Color is null && patch.Order is null && patch.TimeZone is null)
            throw InvalidInput();
        ValidateProperty(patch.DisplayName, ValidateDisplayName);
        ValidateProperty(patch.Description, ValidateDescription);
        ValidateProperty(patch.Color, property => property.Language is null
            && CalendarCollectionPropertyValues.IsWritableColor(property.Value));
        ValidateProperty(patch.TimeZone, property => property.Language is null
            && CalendarCollectionPropertyValues.IsTimeZoneId(property.Value));
        ValidateOrder(patch.Order);
    }

    private static void ValidateProperty(CalendarMetadataTextPatch? property, Func<CalendarMetadataTextPatch, bool> isValidSet)
    {
        if (property is null)
            return;
        if (property.Operation == "remove")
        {
            if (property.Value is not null || property.Language is not null)
                throw InvalidInput();
            return;
        }
        if (property.Operation != "set" || property.Value is null || !isValidSet(property))
            throw InvalidInput();
    }

    private static void ValidateOrder(CalendarMetadataOrderPatch? property)
    {
        if (property is null)
            return;
        var valid = property.Operation switch
        {
            "remove" => property.Value is null,
            "set" => CalendarCollectionPropertyValues.IsWritableOrder(property.Value),
            _ => false
        };
        if (!valid)
            throw InvalidInput();
    }

    private static bool ValidateDisplayName(CalendarMetadataTextPatch property) =>
        property.Language is null && !string.IsNullOrWhiteSpace(property.Value) && IsBoundedXmlText(property.Value, 256);

    private static bool ValidateDescription(CalendarMetadataTextPatch property) =>
        IsBoundedXmlText(property.Value!, 4096)
        && (property.Language is not { } language || language.Length <= 64 && LanguagePattern().IsMatch(language));

    private static bool IsBoundedXmlText(string value, int maximum)
    {
        if (ExceedsTextLimit(value, maximum))
            return false;
        try
        {
            XmlConvert.VerifyXmlChars(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool ExceedsTextLimit(string value, int maximum)
    {
        if (value.Length <= maximum)
            return false;
        var scalars = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++scalars > maximum)
                return true;
        }
        return false;
    }

    internal static string Body(IReadOnlyList<CalendarMetadataPropertyChange> changes) => new XElement(
        CalendarMetadataProtocol.Dav + "propertyupdate",
        new XAttribute(XNamespace.Xmlns + "ical", CalendarCollectionPropertyValues.AppleIcal.NamespaceName),
        changes.Select(Instruction))
        .ToString(SaveOptions.DisableFormatting);

    private static XElement Instruction(CalendarMetadataPropertyChange change)
    {
        var property = new XElement(change.Name);
        if (change.Value is not null)
        {
            property.Value = change.Value;
            if (change.Language is not null)
                property.Add(new XAttribute(XNamespace.Xml + "lang", change.Language));
        }
        return new XElement(CalendarMetadataProtocol.Dav + (change.Value is null ? "remove" : "set"),
            new XElement(CalendarMetadataProtocol.Dav + "prop", property));
    }

    internal static CalendarMetadataPatchDispatch ReadDispatch(
        string href,
        IReadOnlyList<CalendarMetadataPropertyChange> changes,
        CalendarProtocolResponse response)
    {
        if (response.StatusCode != 207)
            return HttpDispatch(response.StatusCode);
        try
        {
            var properties = CalendarMetadataProtocol.ReadProperties(href, response.Body, response.CharSet);
            return PropertyDispatch(changes, properties);
        }
        catch (Exception exception) when (exception is CalendarProtocolException or XmlException)
        {
            return Uncertain();
        }
    }

    // RFC 4918 §9.2: PROPPATCH is atomic, so either every property succeeds or none does and
    // the properties that did not fail report 424. Contradictory per-property truth stays uncertain.
    private static CalendarMetadataPatchDispatch PropertyDispatch(
        IReadOnlyList<CalendarMetadataPropertyChange> changes,
        IReadOnlyDictionary<XName, CalendarMetadataProperty> properties)
    {
        if (properties.Count != changes.Count || changes.Any(change => !properties.ContainsKey(change.Name)))
            return Uncertain();
        var statuses = changes.Select(change => (change.Member, Status: properties[change.Name].StatusCode)).ToArray();
        if (statuses.All(item => item.Status is 200 or 201 or 204))
            return new(CalendarMutationState.Committed, null);
        var rejections = statuses.Where(item => item.Status >= 400)
            .Select(item => new CalendarPropertyRejection(item.Member, item.Status)).ToArray();
        if (rejections.Length == statuses.Length)
        {
            var status = statuses.Select(item => item.Status).FirstOrDefault(value => value != 424, 424);
            return new(CalendarMutationState.NotCommitted, WithRejections(CalendarMetadataProtocol.StatusFailure(status), rejections));
        }
        return new(CalendarMutationState.Unknown, WithRejections(Uncertain().Error!, rejections));
    }

    private static CalendarProtocolException WithRejections(
        CalendarProtocolException exception,
        IReadOnlyList<CalendarPropertyRejection> rejections) =>
        new(exception.Code, exception.Message, exception.Retryable) { RejectedProperties = rejections };

    private static CalendarMetadataPatchDispatch HttpDispatch(int status) => status switch
    {
        >= 400 and < 500 => new(CalendarMutationState.NotCommitted, CalendarMetadataProtocol.StatusFailure(status)),
        501 => new(CalendarMutationState.NotCommitted, CalendarMetadataProtocol.StatusFailure(status)),
        _ => Uncertain()
    };

    internal static bool Matches(IReadOnlyList<CalendarMetadataPropertyChange> changes, CalendarMetadataObservation observed) =>
        changes.All(change => MatchesProperty(change, observed.Properties.GetValueOrDefault(change.Name)));

    private static bool MatchesProperty(CalendarMetadataPropertyChange expected, CalendarMetadataProperty? observed)
    {
        if (observed is null)
            return false;
        if (expected.Value is null)
            return observed.StatusCode == 404;
        return observed.StatusCode == 200 && !observed.Element.Elements().Any() && expected.Matches(observed.Element);
    }

    private static IEnumerable<CalendarMetadataPropertyChange> Changes(CalendarMetadataPatch patch)
    {
        if (patch.DisplayName is not null)
            yield return Text("displayName", CalendarMetadataProtocol.Dav + "displayname", patch.DisplayName);
        if (patch.Description is not null)
            yield return Text("description", CalendarMetadataProtocol.CalDav + "calendar-description", patch.Description);
        if (patch.Color is not null)
            yield return Color(patch.Color.Value);
        if (patch.Order is not null)
            yield return Order(patch.Order.Value);
        if (patch.TimeZone is not null)
            yield return TimeZone(patch.TimeZone.Value);
    }

    private static CalendarMetadataPropertyChange Text(string member, XName name, CalendarMetadataTextPatch patch) =>
        new(member, name, patch.Value, patch.Language, element => element.Value == patch.Value
            && string.Equals(CalendarMetadataProtocol.Language(element), patch.Language, StringComparison.OrdinalIgnoreCase));

    // A server may store an equivalent color with another hex case or an Apple alpha suffix.
    private static CalendarMetadataPropertyChange Color(string? value) =>
        new("color", CalendarCollectionPropertyValues.ColorName, value, null, element =>
            string.Equals(CalendarCollectionPropertyValues.ReadColor(element.Value), value, StringComparison.OrdinalIgnoreCase));

    private static CalendarMetadataPropertyChange Order(int? value) =>
        new("order", CalendarCollectionPropertyValues.OrderName, value?.ToString(CultureInfo.InvariantCulture), null,
            element => CalendarCollectionPropertyValues.ReadOrder(element.Value) == value);

    // The generated VTIMEZONE is not compared lexically: readback verifies its single TZID.
    private static CalendarMetadataPropertyChange TimeZone(string? value) =>
        new("timeZone", CalendarCollectionPropertyValues.TimeZoneName,
            value is null ? null : CalendarCollectionPropertyValues.SerializeTimeZone(value), null,
            element => ReadsTimeZone(element, value!));

    private static bool ReadsTimeZone(XElement element, string expected)
    {
        try
        {
            return CalendarMetadataProtocol.TimeZoneIds(element, CancellationToken.None).SequenceEqual([expected]);
        }
        catch (CalendarProtocolException)
        {
            return false;
        }
    }

    internal static CalendarMetadataPatchDispatch Uncertain() => new(CalendarMutationState.Unknown,
        new CalendarProtocolException("indeterminate", "The property update may have committed. Inspect the Calendar before deciding another write."));

    private static CalendarProtocolException InvalidInput() => new("invalid_input",
        "Set or remove at least one Calendar property. Sets require a bounded value; only description accepts a language tag. "
        + "color is #RRGGBB, order is a non-negative integer, and timeZone is an IANA time zone identifier.");

    [GeneratedRegex("^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex LanguagePattern();
}

/// <summary>
/// One PROPPATCH instruction: a null <see cref="Value"/> removes the property. <see cref="Member"/>
/// is the public patch member used to report per-property rejections.
/// </summary>
internal sealed record CalendarMetadataPropertyChange(
    string Member,
    XName Name,
    string? Value,
    string? Language,
    Func<XElement, bool> Matches);

internal sealed record CalendarMetadataPatchDispatch(CalendarMutationState State, CalendarProtocolException? Error);
