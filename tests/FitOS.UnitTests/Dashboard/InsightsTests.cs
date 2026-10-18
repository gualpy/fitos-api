using System.Net;
using System.Text;
using FitOS.Application.Common;
using FitOS.Application.Dashboard;
using FitOS.Infrastructure.Dashboard;
using FitOS.UnitTests.Members;
using Microsoft.Extensions.Options;

namespace FitOS.UnitTests.Dashboard;

public sealed class InsightsTests
{
    private static readonly Guid Gym = Guid.NewGuid();

    private static readonly DateOnly Today = new(2026, 10, 18);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Clock : ITenantClock
    {
        public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => Task.FromResult(TimeZoneInfo.Utc);

        public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => Task.FromResult(Today);
    }

    private sealed class Repository : IDashboardRepository
    {
        public int Calls { get; private set; }

        public Task<int> CountCustomersAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(10); }

        public Task<int> CountActiveMembersAsync(DateOnly today, CancellationToken cancellationToken) => Task.FromResult(7);

        public Task<int> CountExpiringAsync(DateOnly today, int days, CancellationToken cancellationToken) => Task.FromResult(2);

        public Task<int> CountInsideAsync(CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<IReadOnlyList<GenderCount>> GenderBreakdownAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GenderCount>>([new GenderCount("F", 4), new GenderCount(null, 3)]);

        public Task<IReadOnlyList<DailyCount>> AttendancePerDayAsync(DashboardWindow window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyCount>>(window.Days.Select(d => new DailyCount(d, 3)).ToList());

        public Task<IReadOnlyList<DailyAmount>> SalesPerDayAsync(DashboardWindow window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyAmount>>(window.Days.Select(d => new DailyAmount(d, 5m)).ToList());
    }

    private sealed class Provider(bool configured = true, string answer = " 1. Haz algo.\n") : IInsightsProvider
    {
        public bool IsConfigured { get; } = configured;

        public int Calls { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = prompt;
            return Task.FromResult(answer);
        }
    }

    private static (InsightsService Service, Repository Repo, Provider Provider, MemoryInsightsCache Cache, FixedTime Time) Build(Provider? provider = null, int cacheHours = 6)
    {
        var repo = new Repository();
        var time = new FixedTime(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        var cache = new MemoryInsightsCache(Options.Create(new GeminiOptions { CacheHours = cacheHours }), time);
        provider ??= new Provider();
        return (new InsightsService(new DashboardService(repo, new Clock()), provider, cache, new FixedTenant(Gym), time), repo, provider, cache, time);
    }

    [Fact]
    public async Task Without_a_configured_provider_nothing_is_read_or_sent()
    {
        var (service, repo, provider, _, _) = Build(new Provider(configured: false));

        await Assert.ThrowsAsync<InsightsUnavailableException>(() => service.GetAsync(CancellationToken.None));

        Assert.Equal(0, repo.Calls);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task The_answer_is_trimmed_and_reused_for_the_same_gym_inside_the_window()
    {
        var (service, _, provider, _, time) = Build();

        var first = await service.GetAsync(CancellationToken.None);
        time.Now = time.Now.AddHours(5);
        var second = await service.GetAsync(CancellationToken.None);

        Assert.Equal("1. Haz algo.", first.Text);
        Assert.Equal(first, second);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task After_the_window_a_new_answer_is_requested()
    {
        var (service, _, provider, _, time) = Build();

        await service.GetAsync(CancellationToken.None);
        time.Now = time.Now.AddHours(6);
        await service.GetAsync(CancellationToken.None);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_blank_answer_is_a_failure_and_is_not_cached()
    {
        var (service, _, _, cache, _) = Build(new Provider(answer: "  \n "));

        await Assert.ThrowsAsync<InsightsFailedException>(() => service.GetAsync(CancellationToken.None));

        Assert.Null(cache.Find(Gym));
    }

    [Fact]
    public async Task The_prompt_carries_figures_and_the_money_but_nothing_that_identifies_a_person()
    {
        var (service, _, provider, _, _) = Build();

        await service.GetAsync(CancellationToken.None);

        var prompt = provider.LastPrompt!;
        Assert.Contains("Socios registrados: 10", prompt);
        Assert.Contains("Socios con plan vigente: 7", prompt);
        Assert.Contains("F=4", prompt);
        Assert.Contains("sin indicar=3", prompt);
        Assert.Contains("35.00 USD", prompt); // 7 days x 5
        Assert.DoesNotContain(Gym.ToString(), prompt);
    }

    [Fact]
    public void Without_money_rights_the_prompt_leaves_revenue_out()
    {
        var summary = new DashboardSummary(Today, 1, 1, 0, 7, 0, 0, [], [], null);

        Assert.DoesNotContain("USD", InsightsPrompt.Build(summary));
    }

    [Fact]
    public void Each_gym_has_its_own_cached_answer()
    {
        var (_, _, _, cache, time) = Build();
        var other = Guid.NewGuid();

        cache.Store(Gym, new Insights("mine", time.Now));

        Assert.Null(cache.Find(other));
        Assert.Equal("mine", cache.Find(Gym)!.Text);
    }

    // ---------------------------------------------------------------------------------------------- provider

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static GeminiInsightsProvider Gemini(StubHandler handler, string key = "test-key-not-real", string model = "some-model") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, Options.Create(new GeminiOptions { ApiKey = key, Model = model }));

    [Fact]
    public async Task The_key_travels_in_a_header_never_in_the_url_and_the_text_is_joined()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"uno "},{"text":"dos"}]}}]}""");

        var text = await Gemini(handler).GenerateAsync("hola", CancellationToken.None);

        Assert.Equal("uno dos", text);
        Assert.Equal("https://example.test/v1beta/models/some-model:generateContent", handler.Request!.RequestUri!.ToString());
        Assert.Equal("test-key-not-real", Assert.Single(handler.Request.Headers.GetValues("x-goog-api-key")));
        Assert.DoesNotContain("test-key-not-real", handler.Request.RequestUri.ToString());
    }

    [Fact]
    public async Task A_provider_error_becomes_a_failure_that_does_not_echo_the_body_or_the_key()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid: test-key-not-real"}}""");

        var failure = await Assert.ThrowsAsync<InsightsFailedException>(() => Gemini(handler).GenerateAsync("hola", CancellationToken.None));

        Assert.DoesNotContain("test-key-not-real", failure.Message);
        Assert.Contains("400", failure.Message);
    }

    [Fact]
    public async Task An_answer_without_candidates_is_blank_not_a_crash()
    {
        var text = await Gemini(new StubHandler(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}""")).GenerateAsync("hola", CancellationToken.None);

        Assert.Equal(string.Empty, text);
    }

    [Theory]
    [InlineData("", "model", false)]
    [InlineData("key", "", false)]
    [InlineData("  ", "model", false)]
    [InlineData("key", "model", true)]
    public void It_is_configured_only_with_both_a_key_and_a_model(string key, string model, bool expected) =>
        Assert.Equal(expected, Gemini(new StubHandler(HttpStatusCode.OK, "{}"), key, model).IsConfigured);
}
