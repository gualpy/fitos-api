using System.Text.RegularExpressions;
using FitOS.Application.Auth;
using FitOS.Application.Email;
using FitOS.Application.Subscriptions;
using FitOS.Application.Secrets;
using FitOS.Domain.Identification;
using FitOS.Domain.Tenancy;

namespace FitOS.Application.Onboarding;

/// <summary>
/// Creates a gym (tenant) with its owner and fiscal configuration. Platform administrators only
/// (enforced at the API). The SICOFAC company and its API key are created beforehand in SICOFAC.
/// </summary>
public sealed partial class TenantOnboardingService(
    ITenantOnboardingStore store,
    IPasswordService passwords,
    ISecretProtector secrets,
    PasswordSetupService setup,
    SubscriptionPolicy subscription,
    TimeProvider clock)
{
    public static readonly IReadOnlySet<string> Regimes = new HashSet<string>(StringComparer.Ordinal)
    {
        "general", "rimpe_emprendedor", "rimpe_negocio_popular",
    };

    public async Task<OnboardTenantResult> OnboardAsync(OnboardTenantCommand command, Guid actorId, CancellationToken cancellationToken)
    {
        // Usernames are case-insensitive (citext); store them lowercase so they read the same everywhere.
        command = command with { OwnerUsername = command.OwnerUsername.Trim().ToLowerInvariant(), OwnerEmail = command.OwnerEmail.Trim() };
        Validate(command);

        var tenantId = Guid.NewGuid();
        // The owner never receives a password: the hash is of a random value nobody sees, and a one-time link sets the real one.
        var unusablePassword = TemporaryPassword.Generate(32);
        var key = command.ProviderApiKey.Trim();

        var tenant = new NewTenant(
            tenantId,
            command.Slug,
            command.Name.Trim(),
            command.Ruc,
            command.LegalName.Trim(),
            string.IsNullOrWhiteSpace(command.TradeName) ? null : command.TradeName.Trim(),
            command.HeadOfficeAddress.Trim(),
            command.Regime,
            command.AccountingRequired,
            command.Environment,
            command.ProviderCompanyId.Trim(),
            secrets.Protect(key, tenantId),
            key.Length <= 4 ? key : key[^4..],
            command.SriEstablishment,
            command.EmissionPointCode,
            command.OwnerUsername,
            command.OwnerDisplayName.Trim(),
            command.OwnerEmail,
            passwords.Hash(unusablePassword),
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(subscription.TrialDays));

        var ownerId = await store.CreateAsync(tenant, actorId, cancellationToken);

        // The gym exists whatever happens to the e-mail; if it fails the operator resends it from the panel.
        var sent = true;
        try
        {
            await setup.SendWelcomeAsync(
                new WelcomeRecipient(ownerId, command.OwnerEmail, tenant.OwnerDisplayName, tenant.OwnerUsername, tenant.Name, tenant.Slug),
                cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            sent = false;
        }

        return new OnboardTenantResult(tenantId, command.Slug, ownerId, tenant.OwnerUsername, sent);
    }

    private static void Validate(OnboardTenantCommand c)
    {
        var errors = new Dictionary<string, string>();

        if (!TenantSlug.IsWellFormed(c.Slug))
        {
            errors["slug"] = "invalid_format";
        }
        else if (TenantSlug.Reserved.Contains(c.Slug))
        {
            errors["slug"] = "reserved";
        }

        if (!EcuadorId.IsValidRuc(c.Ruc))
        {
            errors["ruc"] = "invalid";
        }

        Require(errors, "name", c.Name, 120);
        Require(errors, "legalName", c.LegalName, 300);
        Require(errors, "headOfficeAddress", c.HeadOfficeAddress, 300);
        Require(errors, "ownerDisplayName", c.OwnerDisplayName, 120);
        Require(errors, "providerCompanyId", c.ProviderCompanyId, 50);
        Require(errors, "providerApiKey", c.ProviderApiKey, 500);

        if (!UsernamePattern().IsMatch(c.OwnerUsername))
        {
            errors["ownerUsername"] = "invalid_format";
        }

        if (c.OwnerEmail.Length is 0 or > 254 || !EmailPattern().IsMatch(c.OwnerEmail))
        {
            errors["ownerEmail"] = string.IsNullOrWhiteSpace(c.OwnerEmail) ? "required" : "invalid_format";
        }

        if (!Regimes.Contains(c.Regime))
        {
            errors["regime"] = "invalid";
        }

        if (c.Environment is not ("test" or "production"))
        {
            errors["environment"] = "invalid";
        }

        if (!ThreeDigits().IsMatch(c.SriEstablishment))
        {
            errors["sriEstablishment"] = "invalid_format";
        }

        if (!ThreeDigits().IsMatch(c.EmissionPointCode))
        {
            errors["emissionPointCode"] = "invalid_format";
        }

        if (errors.Count > 0)
        {
            throw new OnboardingValidationException(errors);
        }
    }

    private static void Require(Dictionary<string, string> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = "required";
        }
        else if (value.Trim().Length > maxLength)
        {
            errors[field] = "too_long";
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,49}$")]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex("^[0-9]{3}$")]
    private static partial Regex ThreeDigits();
}
