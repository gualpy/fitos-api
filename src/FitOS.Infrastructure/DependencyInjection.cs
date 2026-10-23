using FitOS.Application.Abstractions;
using FitOS.Application.Auth;
using FitOS.Application.Attendance;
using FitOS.Application.Dashboard;
using FitOS.Infrastructure.Dashboard;
using FitOS.Application.Common;
using FitOS.Application.Email;
using FitOS.Application.Platform;
using FitOS.Application.Subscriptions;
using FitOS.Infrastructure.Email;
using FitOS.Infrastructure.Platform;
using FitOS.Infrastructure.Subscriptions;
using FitOS.Application.Customers;
using FitOS.Application.Measurements;
using FitOS.Application.Memberships;
using FitOS.Application.Onboarding;
using FitOS.Application.Plans;
using FitOS.Application.Products;
using FitOS.Application.Sales;
using FitOS.Application.Settings;
using FitOS.Infrastructure.Measurements;
using FitOS.Infrastructure.Products;
using FitOS.Infrastructure.Sales;
using FitOS.Infrastructure.Settings;
using FitOS.Infrastructure.Attendance;
using FitOS.Infrastructure.Members;
using FitOS.Application.Secrets;
using FitOS.Infrastructure.Onboarding;
using FitOS.Infrastructure.Secrets;
using FitOS.Infrastructure.Auth;
using FitOS.Application.Fiscal;
using FitOS.Domain.Sales;
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
        services.AddSingleton<IPasswordService>(sp => new IdentityPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>().Value.PasswordIterations));
        services.AddScoped<ISecurityLog, PostgresSecurityLog>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<ILoginStore, PostgresLoginStore>();
        services.AddSingleton(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            return new LoginPolicy(auth.MaxFailedAttempts, TimeSpan.FromMinutes(auth.LockoutMinutes));
        });
        services.AddScoped<ISessionStore, PostgresSessionStore>();
        services.AddScoped<SessionService>();
        services.AddScoped<LoginService>();
        services.AddScoped<IPasswordStore, PostgresPasswordStore>();
        services.AddScoped<ChangePasswordService>();

        services.AddOptions<SecretsOptions>().Bind(configuration.GetSection(SecretsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        if (configuration.GetSection(EmailOptions.Section).Get<EmailOptions>()?.IsConfigured == true)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            // Without a mail server the message goes to the log: Program.cs refuses to start like this outside Development.
            services.AddSingleton<IEmailSender, LogEmailSender>();
        }

        services.AddSingleton(new SetupLinkPolicy(
            configuration["App:PublicUrl"] ?? string.Empty,
            TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Auth:SetupLinkHours", 48), 1, 168))));
        services.AddSingleton(new SubscriptionPolicy(
            Math.Clamp(configuration.GetValue("Subscription:TrialDays", 30), 1, 365),
            Math.Clamp(configuration.GetValue("Subscription:ReminderDays", 3), 0, 60)));
        services.AddScoped<IPasswordSetupStore, PostgresPasswordSetupStore>();
        services.AddScoped<PasswordSetupService>();
        services.AddScoped<IPlatformStore, PostgresPlatformStore>();
        services.AddScoped<PlatformService>();
        services.AddScoped<ISubscriptionReader, SubscriptionReader>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<ITenantOnboardingStore, PostgresTenantOnboardingStore>();
        services.AddScoped<TenantOnboardingService>();
        services.AddScoped<ITenantFiscalAccountStore, PostgresTenantFiscalAccountStore>();
        services.AddScoped<FiscalConnectionService>();

        services.AddScoped<TenantSettingsReader>();
        services.AddScoped<ITenantClock, TenantClock>();
        services.AddScoped<ITenantSettings, TenantSettings>();
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<PlanService>();
        // Development only: Customers:ValidateIdentification=false skips the cédula/RUC check digits.
        var validateIdentification = configuration.GetValue("Customers:ValidateIdentification", true);
        services.AddScoped(sp => new CustomerService(
            sp.GetRequiredService<ICustomerRepository>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<ITenantClock>(),
            validateIdentification));
        services.AddScoped<MembershipService>();
        services.AddScoped<MembershipSaleService>();
        services.AddScoped<IAttendanceRepository, AttendanceRepository>();
        services.AddScoped<IBranchRepository, BranchRepository>();
        services.AddScoped<IBusinessHoursRepository, BusinessHoursRepository>();
        services.AddScoped<AttendanceService>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ProductService>();
        services.AddOptions<SalesOptions>().Bind(configuration.GetSection(SalesOptions.Section))
            .Validate(o => PaymentMethods.All.All(m => o.PaymentCodes.TryGetValue(m, out var c) && c.Length == 2 && c.All(char.IsAsciiDigit)),
                "Sales:PaymentCodes needs a two-digit SRI code for every payment method.")
            .ValidateOnStart();
        services.AddSingleton<IPaymentCodes, ConfiguredPaymentCodes>();
        services.AddScoped<ISaleRepository, SaleRepository>();
        services.AddScoped<SaleService>();
        services.AddScoped<IMeasurementRepository, MeasurementRepository>();
        services.AddScoped<MeasurementService>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<SettingsService>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<DashboardService>();
        services.Configure<GeminiOptions>(configuration.GetSection("Gemini"));
        services.AddSingleton<IInsightsCache, MemoryInsightsCache>();
        services.AddHttpClient<IInsightsProvider, GeminiInsightsProvider>((sp, client) =>
        {
            var gemini = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            if (Uri.TryCreate(gemini.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Max(gemini.TimeoutSeconds, 1));
        });
        services.AddScoped<InsightsService>();

        return services;
    }
}
