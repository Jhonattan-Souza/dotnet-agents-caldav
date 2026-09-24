# ADR 0008: Local Text Filter with server candidate reduction

Status: Accepted

Date: 2026-09-24

## Context

Entity, Occurrence, and To-do queries could select only by Calendar Scope, Entity Kind, time, and To-do state. A
title or keyword search had to page through a complete result, up to the 5,000-resource and 32 MiB budgets, and
filter it in the model. RFC 4791 section 9.7.5 `text-match` can reduce the server candidates, but its semantics vary:
collations beyond `i;ascii-casemap` and `i;octet` are optional, some servers ignore the collation, and a comp-filter
combines its prop-filters only conjunctively. Radicale 3.7.8 lowercases both sides, ignores the collation and
matches each prop-filter against any component of the resource.

## Decision

All three query Starts accept an optional Text Filter: `text`, split on whitespace into at most 16 terms, and at
most 16 `categories`. Its local evaluation over the authoritative resource is the only result truth:

- every term must be a substring of SUMMARY, DESCRIPTION, LOCATION, or one CATEGORIES value, and every category
  must equal one trimmed CATEGORIES value, all within the same master or Recurrence Override component;
- ASCII letters fold only to ASCII lowercase and other runes use the invariant Unicode lowercase mapping, never
  to ASCII and without normalization;
- an Entity or compact To-do Entity row matches through any component, an Occurrence only through its own effective
  component without inheriting master text, and Opaque Calendar Object Resources never match; and
- a resource without any matching component is dropped before temporal or recurrence evaluation.

A Start may reduce candidates before retrieval. It picks the longest printable-ASCII run of one term, excluding
`\`, `,` and `;`, and sends one `calendar-query` per searched property with an `i;ascii-casemap` `text-match`
for that run. Every category's longest eligible run joins each of these queries. When no term has such a run of
at least three characters, the category runs alone form one query; with neither, no server reduction is sent. The
union of these reports is intersected with the ordinary kind or time-range candidates for the same Calendar and
Entity Kind.

Every locally matching component contains the chosen ASCII run with ASCII-only case differences, so an ASCII,
Unicode, or lowercasing collation returns a superset. The run contains no iCalendar TEXT escape, so escaped and
unescaped comparisons agree. Sending one report per property avoids the optional `anyof` test, which servers
without it treat as a conjunction. Keeping text reports separate from the time-range report avoids requiring one
component to satisfy both, which a strict server evaluates per component. Dropping resources without a matching
component before evaluation makes failures independent of whether the server excluded them.

The superset argument assumes that the server evaluates each prop-filter against every VEVENT or VTODO component
of a resource, including Recurrence Overrides, as RFC 4791 section 9.7.1 requires and Radicale and sabre/dav do.
A server that indexes only the master component would silently exclude resources whose only match is in an
override; the pre-filter cannot detect that. It also assumes RFC 5545-valid TEXT escapes: local decoding is lenient
on invalid escapes such as `a\tb`, which a server may compare differently.

A 405 or 501 response, or a 400 or 403 response carrying `CALDAV:supported-filter` or
`CALDAV:supported-collation`, is retained as unavailable text-match capability for that Calendar and Entity Kind
within the existing authorization- and configuration-scoped capability observations. The query continues with the
unreduced candidates. A text REPORT that exceeds the bounded response read or is rejected with 413, or a 400 or 403
without either precondition, also continues unreduced, because the ordinary candidate REPORT already succeeded
and the local match is the truth; that attempt retains no capability state. Other failures still fail the query,
and the ordinary kind or time-range REPORT keeps its existing failure rules. An empty pre-filter never reaches
the server, and a Calendar without ordinary candidates sends no text REPORT. The closed
`caldav.query.text_prefilter` operation attribute records the least reduced outcome across Calendars:
`unavailable`, `unreduced`, `applied`, or `ineligible`.

The folded criteria are frozen in the Query Result Snapshot. Version 3 cursors bind an HMAC of them, and Continue
still accepts only `cursor` and `pageSize`.

## Consequences

Keyword and tag searches no longer transfer or page through non-matching resources on servers that implement
`text-match`. Results are identical with or without the reduction, although a larger unreduced corpus can exhaust
an execution budget sooner. A term entirely outside printable ASCII still requires local filtering of every
candidate. Phrase, fuzzy, accent-insensitive, and other-property searches are not supported.
