namespace FitOS.Domain.Customers;

public enum CustomerStatus
{
    Active,
    Inactive,
}

/// <summary>A gym member. Mapped to the <c>customers</c> table; <see cref="ClientCode"/> is assigned by the database.</summary>
public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Human-visible correlative, unique per tenant. Zero until the first save.</summary>
    public long ClientCode { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SRI code: 04 RUC, 05 cédula, 06 pasaporte, 08 exterior. Null together with <see cref="Identification"/>.</summary>
    public string? IdentificationType { get; set; }

    public string? Identification { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>M, F or O.</summary>
    public string? Gender { get; set; }

    public DateOnly? BirthDate { get; set; }

    public string? RfidCard { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    public DateOnly JoinedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
