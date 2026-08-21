using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal abstract record CalendarCreationCommand
{
    private CalendarCreationCommand()
    {
    }

    internal sealed record Event(CalendarEventCreateRequest Request) : CalendarCreationCommand;

    internal sealed record Todo(CalendarTodoCreateRequest Request) : CalendarCreationCommand;

    internal sealed record Exact(CalendarReviewedExactCreate ReviewedCreate) : CalendarCreationCommand;
}

internal sealed record ExactCreateIntent(CalendarExactCreateRequest Request);

internal abstract record CalendarCreationOutcome
{
    private CalendarCreationOutcome()
    {
    }

    internal sealed record Semantic(CalendarEntityCreateResult Result) : CalendarCreationOutcome;

    internal sealed record Exact(CalendarExactResourceResult Result) : CalendarCreationOutcome;
}
