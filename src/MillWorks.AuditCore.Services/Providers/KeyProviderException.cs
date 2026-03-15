namespace MillWorks.AuditCore.Services.Encryption.Providers;

/// <summary>
/// Exception for key provider errors
/// </summary>
public sealed class KeyProviderException : Exception
{
    /// <summary>
    /// Key provider exception with message
    /// </summary>
    /// <param name="message"></param>
    public KeyProviderException(string message) : base(message)
    {
    }

    /// <summary>
    /// Key provider exception with inner exception
    /// </summary>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    public KeyProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}