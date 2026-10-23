using FitOS.Application.Common;
using FitOS.Application.Email;
using FitOS.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FitOS.UnitTests.Platform;

public sealed class EmailTests
{
    private static EmailSettingsInput Valid(string? password = "clave-del-buzon") =>
        new("smtp.hostinger.com", 465, "ssl_on_connect", "no-reply@support-me.net", "no-reply@support-me.net", password);

    private static (EmailSettingsService Service, FakeEmailSettingsStore Store, FakeSecretProtector Secrets, FakeSmtp Smtp, FakeLog Log) Settings()
    {
        var store = new FakeEmailSettingsStore();
        var secrets = new FakeSecretProtector();
        var smtp = new FakeSmtp();
        var log = new FakeLog();
        return (new EmailSettingsService(store, secrets, smtp, log), store, secrets, smtp, log);
    }

    [Fact]
    public async Task Saving_encrypts_the_password_and_the_panel_only_sees_its_last_characters()
    {
        var (service, _, _, _, log) = Settings();

        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        var view = await service.GetAsync(CancellationToken.None);
        Assert.True(view.Configured);
        Assert.Equal("••••uzon", view.PasswordHint); // four dots + the last four characters
        Assert.DoesNotContain("clave-del-buzon", System.Text.Json.JsonSerializer.Serialize(view));
        Assert.Contains("email_settings_changed", log.Events.Select(e => e.Name));
        Assert.Equal("ssl_on_connect", view.Security);
    }

    [Fact]
    public async Task Saving_again_without_a_password_keeps_the_stored_one()
    {
        var (service, store, _, _, _) = Settings();
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);
        var before = store.Stored!.PasswordEnc;

        await service.SaveAsync(Valid(password: null) with { Port = 587, Security = "start_tls" }, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(before, store.Stored!.PasswordEnc);
        Assert.Equal(587, store.Stored.Port);
        Assert.Equal(SmtpSecurity.StartTls, store.Stored.Security);
    }

    [Fact]
    public async Task The_first_save_needs_a_password()
    {
        var (service, store, _, _, _) = Settings();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(Valid(password: ""), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("required", ex.Errors["password"]);
        Assert.Null(store.Stored);
    }

    [Theory]
    [InlineData("host", "", "required")]
    [InlineData("host", "smtp host", "invalid")]
    [InlineData("host", "-bad.example.com", "invalid")]
    [InlineData("port", "0", "invalid")]
    [InlineData("port", "70000", "invalid")]
    [InlineData("security", "plain", "invalid")]
    [InlineData("fromAddress", "", "required")]
    [InlineData("fromAddress", "sin-arroba", "invalid")]
    public async Task Invalid_settings_are_rejected_and_nothing_is_stored(string field, string value, string code)
    {
        var (service, store, _, _, _) = Settings();
        var input = field switch
        {
            "host" => Valid() with { Host = value },
            "port" => Valid() with { Port = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "security" => Valid() with { Security = value },
            _ => Valid() with { FromAddress = value },
        };

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => service.SaveAsync(input, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(code, ex.Errors[field]);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task The_test_message_goes_through_the_saved_settings_with_the_decrypted_password()
    {
        var (service, _, _, smtp, log) = Settings();
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        await service.SendTestAsync(" yo@example.test ", Guid.NewGuid(), CancellationToken.None);

        var (settings, message) = Assert.Single(smtp.Sent);
        Assert.Equal("clave-del-buzon", settings.Password);
        Assert.Equal("smtp.hostinger.com", settings.Host);
        Assert.Equal(SmtpSecurity.SslOnConnect, settings.Security);
        Assert.Equal("yo@example.test", message.To);
        Assert.Contains("email_test_sent", log.Events.Select(e => e.Name));
    }

    [Fact]
    public async Task The_test_needs_saved_settings_and_a_valid_recipient()
    {
        var (service, _, _, _, _) = Settings();

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.SendTestAsync("nope", Guid.NewGuid(), CancellationToken.None));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.SendTestAsync("yo@example.test", Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("email_not_configured", ex.Code);
    }

    private static (ConfiguredEmailSender Sender, FakeEmailSettingsStore Store, FakeSmtp Smtp, FakeLog Log, FakeSecretProtector Secrets, EmailSettingsService Service) Sender(
        EmailOptions? env = null, bool logInsteadOfSending = false)
    {
        var (service, store, secrets, smtp, log) = Settings();
        var sender = new ConfiguredEmailSender(store, service, smtp, Options.Create(env ?? new EmailOptions()), log, NullLogger<ConfiguredEmailSender>.Instance, logInsteadOfSending);
        return (sender, store, smtp, log, secrets, service);
    }

    private static readonly EmailOptions Env = new() { Host = "env.example.test", From = "env@example.test", Username = "env", Password = "env-pass" };

    private static readonly EmailMessage Mail = new("socio@example.test", "Hola", "cuerpo", "Gimnasio Titan");

    [Fact]
    public async Task The_database_settings_win_over_the_environment()
    {
        var (sender, _, smtp, _, _, service) = Sender(Env);
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);

        await sender.SendAsync(Mail, CancellationToken.None);

        Assert.Equal("smtp.hostinger.com", Assert.Single(smtp.Sent).Settings.Host);
        Assert.Equal("Gimnasio Titan", smtp.Sent[0].Message.FromName);
    }

    [Fact]
    public async Task Without_database_settings_the_environment_is_used()
    {
        var (sender, _, smtp, _, _, _) = Sender(Env);

        await sender.SendAsync(Mail, CancellationToken.None);

        Assert.Equal("env.example.test", Assert.Single(smtp.Sent).Settings.Host);
    }

    [Fact]
    public async Task A_failing_database_server_falls_back_to_the_environment_once_and_leaves_a_trace()
    {
        var (sender, _, smtp, log, _, service) = Sender(Env);
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);
        smtp.FailHosts["smtp.hostinger.com"] = "auth_failed";

        await sender.SendAsync(Mail, CancellationToken.None);

        Assert.Equal("env.example.test", Assert.Single(smtp.Sent).Settings.Host);
        var trace = Assert.Single(log.Events, e => e.Name == "email_settings_fallback");
        Assert.Equal("auth_failed", trace.Detail!["reason"]);
    }

    [Fact]
    public async Task An_unreadable_database_password_also_falls_back()
    {
        var (sender, _, smtp, _, secrets, service) = Sender(Env);
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);
        secrets.Broken = true; // the encryption key changed

        await sender.SendAsync(Mail, CancellationToken.None);

        Assert.Equal("env.example.test", Assert.Single(smtp.Sent).Settings.Host);
    }

    [Fact]
    public async Task A_failing_database_server_without_a_fallback_surfaces_the_error()
    {
        var (sender, _, smtp, _, _, service) = Sender();
        await service.SaveAsync(Valid(), Guid.NewGuid(), CancellationToken.None);
        smtp.FailHosts["smtp.hostinger.com"] = "auth_failed";

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(Mail, CancellationToken.None));

        Assert.Equal("auth_failed", ex.Reason);
    }

    [Fact]
    public async Task With_nothing_configured_it_fails_clearly_except_in_development_where_it_only_logs()
    {
        var (strict, _, strictSmtp, _, _, _) = Sender();
        var (dev, _, devSmtp, _, _, _) = Sender(logInsteadOfSending: true);

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => strict.SendAsync(Mail, CancellationToken.None));
        await dev.SendAsync(Mail, CancellationToken.None);

        Assert.Equal("not_configured", ex.Reason);
        Assert.Empty(strictSmtp.Sent);
        Assert.Empty(devSmtp.Sent);
    }

    [Fact]
    public void The_gym_name_becomes_the_display_name_and_cannot_inject_headers()
    {
        var settings = new SmtpSettings("h", 465, SmtpSecurity.SslOnConnect, "u", "p", "no-reply@support-me.net", "FitOS");

        var mime = MailKitDelivery.Build(settings, new EmailMessage("socio@example.test", "Hola\r\nBcc: robo@example.test", "x", "Gimnasio Titan\r\nBcc: robo@example.test"));

        var from = Assert.Single(mime.From.Mailboxes);
        Assert.Equal("no-reply@support-me.net", from.Address); // the address never changes
        Assert.DoesNotContain("\n", from.Name ?? string.Empty);
        Assert.DoesNotContain("\r", from.Name ?? string.Empty);
        Assert.StartsWith("Gimnasio Titan", from.Name ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(mime.Bcc);
        Assert.DoesNotContain("\n", mime.Subject ?? string.Empty);
    }

    [Fact]
    public void Without_a_gym_name_the_sender_is_fitos_and_a_long_name_is_cut()
    {
        var settings = new SmtpSettings("h", 465, SmtpSecurity.SslOnConnect, "u", "p", "no-reply@support-me.net", "FitOS");

        Assert.Equal("FitOS", Assert.Single(MailKitDelivery.Build(settings, new EmailMessage("a@example.test", "s", "b")).From.Mailboxes).Name);
        Assert.Equal(80, MailKitDelivery.CleanName(new string('x', 200)).Length);
        Assert.Equal("FitOS", MailKitDelivery.CleanName("  \r\n "));
    }

    [Fact]
    public void An_unparseable_recipient_is_an_invalid_address()
    {
        var settings = new SmtpSettings("h", 465, SmtpSecurity.SslOnConnect, "u", "p", "no-reply@support-me.net", "FitOS");

        var ex = Assert.Throws<EmailDeliveryException>(() => MailKitDelivery.Build(settings, new EmailMessage("no es un correo", "s", "b")));

        Assert.Equal("invalid_address", ex.Reason);
    }
}
