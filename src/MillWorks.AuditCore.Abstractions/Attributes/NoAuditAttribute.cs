namespace MillWorks.AuditCore.Abstractions.Attributes;

/// <summary>
/// NoAuditAttribute is used to mark classes or properties that should not be audited.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class NoAuditAttribute : Attribute
{
}
