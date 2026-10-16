namespace FitOS.Application.Fiscal;

/// <summary>
/// Failure reported by (or while reaching) the fiscal provider. <see cref="IsRetryable"/> tells the
/// outbox worker whether to retry with the SAME idempotency key or give up and alert.
/// </summary>
public sealed class FiscalProviderException : Exception
{
    public FiscalProviderException(string code, string message, int? httpStatus, bool isRetryable, string? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
        IsRetryable = isRetryable;
        Details = details;
    }

    /// <summary>Stable code from the contract (e.g. <c>idempotency_key_reused</c>) or <c>network_error</c>.</summary>
    public string Code { get; }

    public int? HttpStatus { get; }

    public bool IsRetryable { get; }

    public string? Details { get; }
}
