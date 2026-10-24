using System.Text;
using System.Text.RegularExpressions;

namespace FitOS.Application.Email;

/// <summary>One label/value line of an e-mail's details box (gym, username, IP…).</summary>
public sealed record EmailDetail(string Label, string Value);

/// <summary>
/// The content of a transactional e-mail, rendered twice: a plain-text body (always present, for clients that block HTML)
/// and an HTML body with the brand on top, a button and the link repeated underneath. Every value is HTML-encoded, so a
/// gym or person name can never inject markup.
/// </summary>
/// <param name="Brand">What the header shows: the gym's name, or FitOS for platform messages.</param>
/// <param name="Paragraphs">Plain text; wrap a name in <see cref="Emphasis"/> to bold it in the HTML version.</param>
public sealed record EmailCard(
    string Brand,
    string Title,
    IReadOnlyList<string> Paragraphs,
    string Footer,
    IReadOnlyList<EmailDetail>? Details = null,
    string? ButtonText = null,
    string? Url = null,
    string? Warning = null,
    string? Note = null)
{
    private static readonly Regex Strong = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <summary>Marks a value as bold in the HTML version (and plain in the text one). Asterisks inside it are dropped.</summary>
    public static string Emphasis(string value) => "**" + value.Replace("*", string.Empty, StringComparison.Ordinal) + "**";

    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var paragraph in Paragraphs)
        {
            text.Append(Plain(paragraph)).Append("\n\n");
        }

        if (Details is { Count: > 0 })
        {
            foreach (var detail in Details)
            {
                text.Append(detail.Label).Append(": ").Append(detail.Value).Append('\n');
            }

            text.Append('\n');
        }

        if (Warning is not null)
        {
            text.Append(Plain(Warning)).Append("\n\n");
        }

        if (ButtonText is not null && Url is not null)
        {
            text.Append(ButtonText).Append(":\n").Append(Url).Append("\n\n");
        }

        if (Note is not null)
        {
            text.Append(Plain(Note)).Append("\n\n");
        }

        text.Append("--\n").Append(Footer).Append('\n');
        return text.ToString();
    }

    public string ToHtml()
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>");
        html.Append("<body style=\"margin:0;padding:0;background:#e6eaf0;\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#e6eaf0;padding:24px 12px;\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:10px;font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.55;color:#1c2633;\">");

        html.Append("<tr><td style=\"padding:22px 32px 18px;border-bottom:1px solid #e3e8ef;\">")
            .Append("<span style=\"font-family:'Arial Narrow',Arial,sans-serif;font-weight:700;font-size:24px;letter-spacing:.02em;text-transform:uppercase;color:#1c2633;\">")
            .Append(Encode(Brand)).Append("</span></td></tr>");

        html.Append("<tr><td style=\"padding:28px 32px 8px;\">");
        html.Append("<h2 style=\"font-size:20px;line-height:1.3;margin:0 0 14px;color:#1c2633;\">").Append(Encode(Title)).Append("</h2>");
        foreach (var paragraph in Paragraphs)
        {
            html.Append("<p style=\"margin:0 0 14px;\">").Append(Rich(paragraph)).Append("</p>");
        }

        if (Details is { Count: > 0 })
        {
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f3f6fa;border-radius:8px;margin:0 0 18px;\"><tr><td style=\"padding:10px 16px;\">");
            foreach (var detail in Details)
            {
                html.Append("<div style=\"padding:3px 0;\"><span style=\"color:#66758a;\">").Append(Encode(detail.Label))
                    .Append(":</span> <b style=\"word-break:break-all;\">").Append(Encode(detail.Value)).Append("</b></div>");
            }

            html.Append("</td></tr></table>");
        }

        if (Warning is not null)
        {
            html.Append("<div style=\"background:#fff4e0;color:#7a4b00;border-radius:8px;padding:12px 16px;margin:0 0 18px;font-size:14px;\">")
                .Append(Rich(Warning)).Append("</div>");
        }

        if (ButtonText is not null && Url is not null)
        {
            var url = Encode(Url);
            html.Append("<p style=\"margin:6px 0 16px;\"><a href=\"").Append(url)
                .Append("\" style=\"display:inline-block;background:#1f5fd6;color:#ffffff;text-decoration:none;font-weight:700;padding:13px 26px;border-radius:8px;\">")
                .Append(Encode(ButtonText)).Append("</a></p>");
            html.Append("<p style=\"margin:0 0 6px;font-size:13px;color:#66758a;\">Si el botón no funciona, copia esta dirección en tu navegador:</p>");
            html.Append("<p style=\"margin:0 0 14px;font-size:12px;color:#66758a;word-break:break-all;\">").Append(url).Append("</p>");
        }

        if (Note is not null)
        {
            html.Append("<p style=\"margin:0 0 14px;font-size:13px;color:#66758a;\">").Append(Rich(Note)).Append("</p>");
        }

        html.Append("</td></tr>");
        html.Append("<tr><td style=\"padding:16px 32px 24px;border-top:1px solid #e3e8ef;font-size:12px;color:#66758a;\">")
            .Append(Encode(Footer)).Append("</td></tr>");
        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    public EmailMessage ToMessage(string to, string subject, string? fromName = null) =>
        new(to, subject, ToText(), fromName, ToHtml());

    /// <summary>Escapes only what can change the markup; accents stay as they are (the document declares UTF-8).</summary>
    private static string Encode(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Plain(string value) => Strong.Replace(value, "$1");

    /// <summary>Encodes first, then turns the (already safe) emphasis markers into tags.</summary>
    private static string Rich(string value) => Strong.Replace(Encode(value), "<b>$1</b>");
}
