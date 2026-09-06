# ADR 0007: Complete JSON presentation and result budgets

Status: Accepted

All 23 tools return compact JSON in a TextContent block, semantically identical to
structuredContent, following the MCP structured-content compatibility recommendation:
https://modelcontextprotocol.io/specification/draft/server/tools#structured-content.
This includes errors, no_change and confirmation_declined. Resource links, public
schemas and MRTR remain unchanged. QueryPage.HumanText retains its signature and
now carries this compatibility JSON.

Finalization enriches validation errors before generating text, checks budgets,
and observes the facts of the result actually returned. The serialized SDK
CallToolResult must fit within 4 MiB, including structured JSON, escaped textual
JSON and additional content blocks. Results are never truncated to fit. Oversized
results become payload_too_large; mutations retain their actual mutationState.
The replacement error is measured as well.

The separate 64 KiB human-information budget counts serialized messages,
violations, additional text and diagnostics at their contract-defined root, snapshot,
items[*] and items[*].snapshot locations.
Calendar properties and projections are arbitrary data and are not searched for
human-looking field names. Compatibility JSON does not consume this budget again.

Snapshot preparation measures the escaped contribution of each serialized item
once, retaining only its byte count. Page admission includes both representations,
separators, cursor and fixed metadata, and reduces page size as needed. An empty
envelope or individual item that cannot fit fails admission. Continue uses stored
items and does no CalDAV or semantic evaluation.

Installation metadata requires CALDAV_EVALUATION_TIME_ZONE. Manual runs can omit
it when the caller supplies evaluationTimeZone. The explicit argument takes
precedence over validated configuration; invalid arguments fail without fallback.
Missing context identifies both ways to supply an IANA identifier. Every To-do
Start requires context, including queries without a window. Unbounded Entity
queries keep their existing rule. No process-zone inference or MRTR is added.
Historical contracts and source package versions remain unchanged.
