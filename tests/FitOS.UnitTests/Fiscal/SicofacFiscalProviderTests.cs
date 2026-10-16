using System.Net;
using System.Text;
using System.Text.Json;
using FitOS.Application.Fiscal;
using FitOS.Infrastructure.Fiscal;

namespace FitOS.UnitTests.Fiscal;

public sealed class SicofacFiscalProviderTests
{
    private static readonly FiscalAccount Account = new("12", "key-secret-123");

    private static FiscalInvoiceRequest Request(FiscalBuyer? buyer = null) => new(
        "fitos:sale:1",
        [new FiscalItem("PLAN-1", "Plan Mensual", 2m, 20m, 15m)],
        [new FiscalPayment("tarjeta_credito", 46m)],
        buyer);

    private const string AuthorizedBody = """
        {"data":{"id":8123,"external_reference":"fitos:sale:1","status":"authorized","document_code":"01",
         "establishment_code":"001","emission_point":"001","sequential":123,"access_key":"1510202601",
         "authorization_number":"1510202601","authorized_at":"2026-10-15T14:03:09+00:00",
         "totals":{"subtotal":40.0,"tax":6.0,"total":46.0},"ride_url":"/api/companies/12/invoices/8123/ride","error":null}}
        """;

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? SeenBody { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, SeenBody);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (SicofacFiscalProvider Provider, Stub Handler) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new Stub(respond);
        return (new SicofacFiscalProvider(new HttpClient(handler) { BaseAddress = new Uri("http://sicofac.test/") }), handler);
    }

    [Fact]
    public async Task Issue_sends_the_contract_request()
    {
        var (provider, handler) = Create((_, _) => Json(HttpStatusCode.Accepted, AuthorizedBody));

        await provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None);

        var seen = handler.Seen!;
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/api/v1/companies/12/invoices/issue", seen.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("key-secret-123", seen.Headers.Authorization.Parameter);
        Assert.Equal("fitos-sale-0001", seen.Headers.GetValues("Idempotency-Key").Single());
        Assert.True(Guid.TryParse(seen.Headers.GetValues("X-Request-Id").Single(), out _));

        using var body = JsonDocument.Parse(handler.SeenBody!);
        var root = body.RootElement;
        Assert.Equal("fitos:sale:1", root.GetProperty("external_reference").GetString());
        Assert.Equal(20m, root.GetProperty("items")[0].GetProperty("unit_price").GetDecimal());
        Assert.Equal(15m, root.GetProperty("items")[0].GetProperty("tax_rate").GetDecimal());
        Assert.Equal("tarjeta_credito", root.GetProperty("payment_methods")[0].GetProperty("method").GetString());
    }

    [Fact]
    public async Task Missing_buyer_and_optional_fields_are_omitted_so_the_provider_uses_consumidor_final()
    {
        var (provider, handler) = Create((_, _) => Json(HttpStatusCode.Accepted, AuthorizedBody));

        await provider.IssueInvoiceAsync(Account, Request(buyer: null), "fitos-sale-0001", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.SeenBody!);
        Assert.False(body.RootElement.TryGetProperty("buyer", out _));
        Assert.False(body.RootElement.TryGetProperty("establishment_code", out _));
        Assert.False(body.RootElement.TryGetProperty("expected_totals", out _));
    }

    [Fact]
    public async Task Accepted_response_maps_to_state_and_is_not_a_replay()
    {
        var (provider, _) = Create((_, _) => Json(HttpStatusCode.Accepted, AuthorizedBody));

        var state = await provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None);

        Assert.Equal(8123, state.Id);
        Assert.Equal(FiscalRemoteStatus.Authorized, state.Status);
        Assert.Equal(123, state.Sequential);
        Assert.Equal("1510202601", state.AccessKey);
        Assert.Equal(new FiscalTotals(40m, 6m, 46m), state.Totals);
        Assert.Null(state.ErrorDetails);
        Assert.False(state.Replayed);
    }

    [Fact]
    public async Task Ok_response_is_flagged_as_replay()
    {
        var (provider, _) = Create((_, _) => Json(HttpStatusCode.OK, AuthorizedBody));

        var state = await provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None);

        Assert.True(state.Replayed);
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("\"001-001-000000123\"", 123L)]
    [InlineData("null", null)]
    public async Task Sequential_accepts_number_or_formatted_string(string json, long? expected)
    {
        var body = AuthorizedBody.Replace("\"sequential\":123", $"\"sequential\":{json}");
        var (provider, _) = Create((_, _) => Json(HttpStatusCode.Accepted, body));

        var state = await provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None);

        Assert.Equal(expected, state.Sequential);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused", false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "validation_failed", false)]
    [InlineData(HttpStatusCode.Conflict, "totals_mismatch", false)]
    [InlineData(HttpStatusCode.Conflict, "external_reference_conflict", false)]
    [InlineData(HttpStatusCode.Unauthorized, "unauthenticated", false)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable", true)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", true)]
    public async Task Contract_errors_keep_their_code_and_retry_policy(HttpStatusCode status, string code, bool retryable)
    {
        var body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"nope\",\"details\":{\"field\":[\"x\"]}}}";
        var (provider, _) = Create((_, _) => Json(status, body));

        var ex = await Assert.ThrowsAsync<FiscalProviderException>(
            () => provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal((int)status, ex.HttpStatus);
        Assert.Equal(retryable, ex.IsRetryable);
        Assert.Contains("field", ex.Details);
    }

    [Fact]
    public async Task Non_json_error_page_becomes_a_generic_retryable_error()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var ex = await Assert.ThrowsAsync<FiscalProviderException>(
            () => provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None));

        Assert.Equal("http_502", ex.Code);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task Network_failure_is_retryable()
    {
        var (provider, _) = Create((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<FiscalProviderException>(
            () => provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None));

        Assert.Equal("network_error", ex.Code);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed_as_a_provider_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (provider, _) = Create((_, _) => Json(HttpStatusCode.Accepted, AuthorizedBody));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", cts.Token));
    }

    [Fact]
    public async Task Unknown_remote_status_is_a_non_retryable_error()
    {
        var (provider, _) = Create((_, _) => Json(HttpStatusCode.Accepted, AuthorizedBody.Replace("\"authorized\"", "\"teleported\"")));

        var ex = await Assert.ThrowsAsync<FiscalProviderException>(
            () => provider.IssueInvoiceAsync(Account, Request(), "fitos-sale-0001", CancellationToken.None));

        Assert.Equal("unexpected_status", ex.Code);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Get_by_reference_returns_null_when_unknown_and_escapes_the_reference()
    {
        var (provider, handler) = Create((_, _) => Json(HttpStatusCode.NotFound, """{"error":{"code":"invoice_not_found","message":"x"}}"""));

        var result = await provider.GetByReferenceAsync(Account, "fitos:sale:7/a b", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("fitos%3Asale%3A7%2Fa%20b", handler.Seen!.RequestUri!.OriginalString);
    }

    [Fact]
    public async Task Get_by_reference_returns_the_invoice()
    {
        var (provider, handler) = Create((_, _) => Json(HttpStatusCode.OK, AuthorizedBody));

        var state = await provider.GetByReferenceAsync(Account, "fitos:sale:1", CancellationToken.None);

        Assert.Equal(FiscalRemoteStatus.Authorized, state!.Status);
        Assert.Equal(HttpMethod.Get, handler.Seen!.Method);
        Assert.Equal("key-secret-123", handler.Seen.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void Fiscal_account_never_prints_the_api_key()
    {
        var text = Account.ToString();

        Assert.DoesNotContain("key-secret-123", text);
        Assert.Contains("12", text);
    }
}
