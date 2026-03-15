using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Abstractions.Extensions;

/// <summary>
/// Extension methods for ValidationSeverity
/// </summary>
public static class ValidationSeverityExtensions
{
    /// <summary>
    /// Gets the display name for the severity level
    /// </summary>
    public static string GetDisplayName(this ValidationSeverity severity)
    {
        return severity switch
        {
            ValidationSeverity.Info => "Informational",
            ValidationSeverity.Low => "Low",
            ValidationSeverity.Medium => "Medium",
            ValidationSeverity.High => "High",
            ValidationSeverity.Critical => "Critical",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets the color code for UI display
    /// </summary>
    public static string GetColorCode(this ValidationSeverity severity)
    {
        return severity switch
        {
            ValidationSeverity.Info => "#17a2b8",      // Bootstrap info
            ValidationSeverity.Low => "#28a745",       // Bootstrap success
            ValidationSeverity.Medium => "#ffc107",    // Bootstrap warning
            ValidationSeverity.High => "#fd7e14",      // Bootstrap orange
            ValidationSeverity.Critical => "#dc3545",  // Bootstrap danger
            _ => "#6c757d"                              // Bootstrap secondary
        };
    }

    /// <summary>
    /// Gets the emoji icon for the severity level
    /// </summary>
    public static string GetIcon(this ValidationSeverity severity)
    {
        return severity switch
        {
            ValidationSeverity.Info => "ℹ️",
            ValidationSeverity.Low => "🔵",
            ValidationSeverity.Medium => "🟡",
            ValidationSeverity.High => "🔴",
            ValidationSeverity.Critical => "⚠️",
            _ => "❓"
        };
    }

    /// <summary>
    /// Checks if the severity requires immediate action
    /// </summary>
    public static bool RequiresImmediateAction(this ValidationSeverity severity)
    {
        return severity is ValidationSeverity.Critical or ValidationSeverity.High;
    }
}