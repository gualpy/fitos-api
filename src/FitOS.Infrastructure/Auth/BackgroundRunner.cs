using System.Threading.Channels;
using FitOS.Application.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FitOS.Infrastructure.Auth;

/// <summary>
/// A small in-process queue: each item runs after the response, in a scope of its own (the request's scope is gone by then).
/// Bounded and lossy by design: it only carries best-effort e-mails, and every endpoint that feeds it is rate limited.
/// </summary>
public sealed partial class BackgroundRunner(IServiceScopeFactory scopes, ILogger<BackgroundRunner> logger) : BackgroundService, IBackgroundWork
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Enqueue<TService>(Func<TService, CancellationToken, Task> work)
        where TService : notnull =>
        _channel.Writer.TryWrite((services, ct) => work(services.GetRequiredService<TService>(), ct));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await item(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A background task failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
