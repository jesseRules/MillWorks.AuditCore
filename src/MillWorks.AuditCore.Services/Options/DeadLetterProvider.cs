namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Dead letter queue providers
/// </summary>
public enum DeadLetterProvider
{
    /// <summary>
    /// In-memory storage
    /// </summary>
    InMemory,

    /// <summary>
    /// File system storage
    /// </summary>
    FileSystem,

    /// <summary>
    /// Redis storage
    /// </summary>
    Redis
}