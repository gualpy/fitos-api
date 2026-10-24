using FitOS.Application.Auth;
using FitOS.Application.Email;
using FitOS.Infrastructure.Email;
using MimeKit;

namespace FitOS.UnitTests.Email;

public sealed class EmailCardTests
{
    private static EmailCard Card(string gym = "Titan Gym") => new(
        gym,
        "Tu cuenta está lista",
        [$"Ya puedes administrar {EmailCard.Emphasis(gym)}."],
        "Pie",
        [new EmailDetail("Usuario", "carla")],
        "Crear mi contraseña",
        "https://app.test/#setup=abc",
        Warning: "Vence pronto.",
        Note: "Si no esperabas esto, ignóralo.");

    [Fact]
    public void Names_can_never_inject_markup()
    {
        var html = Card("<script>alert(1)</script>").ToHtml();

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Quotes_in_a_link_cannot_break_out_of_the_attribute()
    {
        var card = Card() with { Url = "https://app.test/#x\" onclick=\"evil()" };

        Assert.DoesNotContain("\" onclick=", card.ToHtml());
    }

    [Fact]
    public void Emphasis_is_bold_in_html_and_plain_in_text_and_a_name_cannot_forge_more()
    {
        var card = Card("Gym **X**");

        Assert.Contains("<b>Gym X</b>", card.ToHtml());
        Assert.DoesNotContain("**", card.ToText());
    }

    [Fact]
    public void The_text_version_has_everything_and_keeps_the_link_on_its_own_line()
    {
        var text = Card().ToText();

        Assert.Contains("Ya puedes administrar Titan Gym.", text);
        Assert.Contains("Usuario: carla", text);
        Assert.Contains("Vence pronto.", text);
        Assert.Contains("\nhttps://app.test/#setup=abc\n", text);
    }

    [Fact]
    public void The_html_shows_the_brand_a_button_and_repeats_the_link()
    {
        var html = Card().ToHtml();

        Assert.Contains("Titan Gym</span>", html);
        Assert.Contains("href=\"https://app.test/#setup=abc\"", html);
        Assert.Contains("Crear mi contraseña</a>", html);
        Assert.Equal(2, html.Split("https://app.test/#setup=abc").Length - 1); // in the button and as text underneath
    }

    [Fact]
    public void The_mime_message_carries_both_versions()
    {
        var message = Card().ToMessage("ana@test.com", "Asunto", "Titan Gym");
        var mime = MailKitDelivery.Build(new SmtpSettings("smtp.test", 465, SmtpSecurity.SslOnConnect, "u", "p", "no-reply@test.com", "FitOS"), message);

        Assert.Equal("Titan Gym", ((MailboxAddress)mime.From[0]).Name);
        Assert.NotNull(mime.TextBody);
        Assert.NotNull(mime.HtmlBody);
        Assert.IsType<MultipartAlternative>(mime.Body);
    }

    [Fact]
    public void A_message_without_html_stays_plain_text()
    {
        var mime = MailKitDelivery.Build(
            new SmtpSettings("smtp.test", 465, SmtpSecurity.SslOnConnect, "u", "p", "no-reply@test.com", "FitOS"),
            new EmailMessage("ana@test.com", "Asunto", "Solo texto"));

        Assert.Equal("Solo texto", mime.TextBody);
        Assert.Null(mime.HtmlBody);
    }
}
