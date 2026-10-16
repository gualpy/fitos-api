using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FitOS.Application.Fiscal;
using Polly;

namespace FitOS.Infrastructure.Fiscal;

/// <summary>
/// <see cref="IFiscalProvider"/> over SICOFAC's API v1 (docs/api-contract-sicofac.md).
/// Authenticates with the per-company API key. Never logs bodies or credentials.
/// </summary>
public sealed class SicofacFiscalProvider(HttpClient http) : IFiscalProvider
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<FiscalInvoiceState> IssueInvoiceAsync(
        FiscalAccount account,
        FiscalInvoiceRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/v1/companies/{Uri.EscapeDataString(account.ProviderCompanyId)}/invoices/issue")
        {
            Content = JsonContent.Create(ToBody(request), options: Json),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await SendAsync(message, account, cancellationToken);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
        {
            return await ReadStateAsync(response, replayed: response.StatusCode == HttpStatusCode.OK, cancellationToken);
        }

        throw await ToExceptionAsync(response, cancellationToken);
    }

    public async Task<FiscalInvoiceState?> GetByReferenceAsync(
        FiscalAccount account,
        string externalReference,
        CancellationToken cancellationToken)
    {
        var path = $"api/v1/companies/{Uri.EscapeDataString(account.ProviderCompanyId)}/invoices/by-reference/{Uri.EscapeDataString(externalReference)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, path);

        using var response = await SendAsync(message, account, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.IsSuccessStatusCode)
        {
            return await ReadStateAsync(response, replayed: false, cancellationToken);
        }

        throw await ToExceptionAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, FiscalAccount account, CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString());

        try
        {
            return await http.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FiscalProviderException("network_error", "SICOFAC is unreachable or timed out.", null, isRetryable: true, inner: ex);
        }
    }

    private static async Task<FiscalInvoiceState> ReadStateAsync(HttpResponseMessage response, bool replayed, CancellationToken cancellationToken)
    {
        DataEnvelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<DataEnvelope>(Json, cancellationToken);
        }
        catch (JsonException ex)
        {
            // The provider accepted the request; replaying the same key returns the same invoice.
            throw new FiscalProviderException("invalid_response", "SICOFAC returned an unreadable body.", (int)response.StatusCode, isRetryable: true, inner: ex);
        }

        var data = envelope?.Data
            ?? throw new FiscalProviderException("invalid_response", "SICOFAC response has no data.", (int)response.StatusCode, isRetryable: true);

        return new FiscalInvoiceState(
            data.Id,
            data.ExternalReference ?? string.Empty,
            ParseStatus(data.Status),
            data.DocumentCode ?? "01",
            data.EstablishmentCode ?? string.Empty,
            data.EmissionPoint ?? string.Empty,
            ParseSequential(data.Sequential),
            data.AccessKey,
            data.AuthorizationNumber,
            data.AuthorizedAt,
            data.Totals is { } t ? new FiscalTotals(t.Subtotal, t.Tax, t.Total) : new FiscalTotals(0, 0, 0),
            data.RideUrl,
            data.Error is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } error ? error.GetRawText() : null,
            replayed);
    }

    private static FiscalRemoteStatus ParseStatus(string? status) => status switch
    {
        "draft" => FiscalRemoteStatus.Draft,
        "processing" => FiscalRemoteStatus.Processing,
        "authorized" => FiscalRemoteStatus.Authorized,
        "rejected" => FiscalRemoteStatus.Rejected,
        "failed" => FiscalRemoteStatus.Failed,
        _ => throw new FiscalProviderException("unexpected_status", $"Unknown invoice status '{status}'.", null, isRetryable: false),
    };

    /// <summary>Accepts both <c>123</c> and <c>"001-001-000000123"</c> (format still open in the contract).</summary>
    internal static long? ParseSequential(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(element.GetString()?.Split('-')[^1], out var parsed) => parsed,
            _ => null,
        };
    }

    private static async Task<FiscalProviderException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var retryable = status is 408 or 429 or >= 500;
        string code = $"http_{status}";
        string message = $"SICOFAC answered HTTP {status}.";
        string? details = null;

        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, cancellationToken);
            if (envelope?.Error is { } error)
            {
                code = error.Code ?? code;
                message = error.Message ?? message;
                details = error.Details?.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error page (proxy, maintenance): keep the generic code.
        }

        return new FiscalProviderException(code, message, status, retryable, details);
    }

    private static IssueBody ToBody(FiscalInvoiceRequest r) => new(
        r.ExternalReference,
        r.DocumentCode,
        r.EstablishmentCode,
        r.EmissionPoint,
        r.Buyer is { } b ? new BuyerBody(b.IdentificationType, b.IdentificationNumber, b.Name, b.Email, b.Phone, b.Address) : null,
        r.Items.Select(i => new ItemBody(i.Code, i.Name, i.Quantity, i.UnitPrice, i.Discount, i.TaxRate)).ToList(),
        r.Payments.Select(p => new PaymentBody(p.Method, p.Value)).ToList(),
        r.ExpectedTotals is { } t ? new TotalsBody(t.Subtotal, t.Tax, t.Total) : null);
}
