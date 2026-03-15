namespace MillWorks.AuditCore.Services.Encryption;

/// <summary>
/// Exception for field encryption errors
/// </summary>
public sealed class FieldEncryptionException : Exception
{
    /// <summary>
    /// FieldEncryptionException constructor
    /// </summary>
    /// <param name="message"></param>
    public FieldEncryptionException(string message) : base(message)
    {
    }

    /// <summary>
    /// FieldEncryptionException constructor with inner exception
    /// </summary>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    public FieldEncryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}