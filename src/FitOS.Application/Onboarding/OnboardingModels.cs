namespace FitOS.Application.Onboarding;

public sealed record OnboardTenantCommand(
    string Slug,
    string Name,
    string Ruc,
    string LegalName,
    string? TradeName,
    string HeadOfficeAddress,
    string Regime,
    bool AccountingRequired,
    string ProviderCompanyId,
    string ProviderApiKey,
    string OwnerUsername,
    string OwnerDisplayName,
    string OwnerEmail,
    string Environment = "test",
    string SriEstablishment = "001",
    string EmissionPointCode = "001");

/// <summary>Everything the store writes in one transaction. Secrets arrive already hashed/encrypted.</summary>
public sealed record NewTenant(
    Guid TenantId,
    string Slug,
    string Name,
    string Ruc,
    string LegalName,
    string? TradeName,
    string HeadOfficeAddress,
    string Regime,
    bool AccountingRequired,
    string Environment,
    string ProviderCompanyId,
    byte[] EncryptedApiKey,
    string ApiKeyHint,
    string SriEstablishment,
    string EmissionPointCode,
    string OwnerUsername,
    string OwnerDisplayName,
    string OwnerEmail,
    string OwnerPasswordHash,
    DateOnly SubscriptionEndsOn);

/// <param name="WelcomeEmailSent">False when the gym was created but the e-mail could not be sent: resend it from the panel.</param>
public sealed record OnboardTenantResult(Guid TenantId, string Slug, Guid OwnerUserId, string OwnerUsername, bool WelcomeEmailSent);

/// <summary>Input rejected before touching the database. <see cref="Errors"/> maps field name to a stable code.</summary>
public sealed class OnboardingValidationException(IReadOnlyDictionary<string, string> errors) : Exception("Invalid onboarding data.")
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>A unique value is already taken. <see cref="Field"/> is <c>slug</c> or <c>ruc</c>.</summary>
public sealed class OnboardingConflictException(string field) : Exception($"{field} is already in use.")
{
    public string Field { get; } = field;
}

public interface ITenantOnboardingStore
{
    /// <summary>Creates tenant, settings, fiscal config, head-office branch, emission point, owner and an audit entry atomically.</summary>
    /// <exception cref="OnboardingConflictException">Slug or RUC already exists; nothing was written.</exception>
    Task<Guid> CreateAsync(NewTenant tenant, Guid actorId, CancellationToken cancellationToken);
}
