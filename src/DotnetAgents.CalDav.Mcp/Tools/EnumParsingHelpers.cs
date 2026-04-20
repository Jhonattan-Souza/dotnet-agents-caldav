using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class EnumParsingHelpers
{
    internal static CalDavTaskStatus ParseTaskStatus(string status)
    {
        var validValues = new[] { "NeedsAction", "InProcess", "Completed", "Cancelled" };

        if (Enum.TryParse<CalDavTaskStatus>(status, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new ArgumentException(
            $"Invalid status value '{status}'. Valid values are: {string.Join(", ", validValues)}.");
    }

    internal static TaskPriority ParseTaskPriority(string priority)
    {
        if (Enum.TryParse<TaskPriority>(priority, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new ArgumentException(
            $"Invalid priority value '{priority}'. Valid values are: None, High, Medium, Low.");
    }
}
