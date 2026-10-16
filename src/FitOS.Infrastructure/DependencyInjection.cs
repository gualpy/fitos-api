using FitOS.Application.Abstractions;
using FitOS.Application.Auth;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Application.Onboarding;
using FitOS.Application.Plans;
using FitOS.Infrastructure.Members;
using FitOS.Application.Secrets;
using FitOS.Infrastructure.Onboarding;
using FitOS.Infrastructure.Secrets;
using FitOS.Infrastructure.Auth;
using FitOS.Application.Fiscal;
using FitOS.Infrastructure.Fiscal;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured (use the fitos_app role, never the owner).");

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<FitOsDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

        services.AddOptions<SicofacOptions>()
            .Bind(configuration.GetSection(SicofacOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var sicofac = configuration.GetSection(SicofacOptions.Section).Get<SicofacOptions>() ?? new SicofacOptions();
        var attempt = TimeSpan.FromSeconds(Math.Clamp(sicofac.AttemptTimeoutSeconds, 1, 120));

        // Retries are safe: every issue call carries the same Idempotency-Key. The outbox worker adds
        // the long-horizon retries (minutes to hours); this layer only absorbs blips.
        services.AddHttpClient<IFiscalProvider, SicofacFiscalProvider>(client =>
            {
                if (Uri.TryCreate(sicofac.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
                {
                    client.BaseAddress = baseAddress;
                }
            })
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = attempt;
                options.Retry.MaxRetryAttempts = 2;
                options.TotalRequestTimeout.Timeout = (attempt * 3) + TimeSpan.FromSeconds(10);
                options.CircuitBreaker.SamplingDuration = attempt * 2;
            });

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordService, IdentityPasswordService>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<ILoginStore, PostgresLoginStore>();
        services.AddSingleton(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            return new LoginPolicy(auth.MaxFailedAttempts, TimeSpan.FromMinutes(auth.LockoutMinutes));
        });
        services.AddScoped<LoginService>();
        services.AddScoped<IPasswordStore, PostgresPasswordStore>();
        services.AddScoped<ChangePasswordService>();

        services.AddOptions<SecretsOptions>().Bind(configuration.GetSection(SecretsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddScoped<ITenantOnboardingStore, PostgresTenantOnboardingStore>();
        services.AddScoped<TenantOnboardingService>();
        services.AddScoped<ITenantFiscalAccountStore, PostgresTenantFiscalAccountStore>();
        services.AddScoped<FiscalConnectionService>();

        services.AddScoped<ITenantClock, TenantClock>();
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<PlanService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<MembershipService>();

        return services;
    }
}
