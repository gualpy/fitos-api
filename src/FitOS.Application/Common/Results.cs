namespace FitOS.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>The thing asked for does not exist FOR THIS TENANT (another tenant's ids look the same).</summary>
public sealed class NotFoundException(string entity) : Exception($"{entity} not found.")
{
    public string Entity { get; } = entity;
}

/// <summary>A uniqueness or consistency rule refused the change. <see cref="Code"/> is a stable machine code.</summary>
public sealed class ConflictException(string code) : Exception($"Conflict: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>Input rejected. <see cref="Errors"/> maps a field name to a stable code.</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string> errors) : Exception("Validation failed.")
{
    public ValidationFailedException(string field, string code)
        : this(new Dictionary<string, string> { [field] = code })
    {
    }

    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>Collects field errors and throws once.</summary>
public sealed class ErrorBag
{
    private readonly Dictionary<string, string> _errors = [];

    public bool HasAny => _errors.Count > 0;

    public void Add(string field, string code) => _errors.TryAdd(field, code);

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
        {
            throw new ValidationFailedException(_errors);
        }
    }
}
