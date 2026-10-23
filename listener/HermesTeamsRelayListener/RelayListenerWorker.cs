using System.Net;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HermesTeamsRelayListener;

/// <summary>
/// On-prem side of the Teams relay, as a hosted background service. Dials OUT
/// to the Azure Relay Hybrid Connection (no inbound firewall port required —
/// this is what replaces the dev tunnel) and forwards every relayed HTTP
/// request to the Hermes gateway's Teams webhook running alongside it.
///
/// Configuration is read via IConfiguration, which resolves each key (below)
/// from, in order: appsettings.json, environment variables, command-line
/// args. That means the same key names work whether you set them as
/// machine-wide environment variables (needed for a Windows Service — a
/// service's environment isn't your interactive terminal session's) or drop
/// them into a local appsettings.json next to the published exe.
/// </summary>
public class RelayListenerWorker : BackgroundService
{
    // Headers HttpRequestMessage manages itself; forwarding them verbatim
    // from the relayed request would conflict with what HttpClient sets.
    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Content-Type"
    };

    private static readonly HttpClient httpClient = new();

    private readonly IConfiguration _configuration;
    private readonly ILogger<RelayListenerWorker> _logger;
    private HybridConnectionListener? _listener;

    public RelayListenerWorker(IConfiguration configuration, ILogger<RelayListenerWorker> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave host StartAsync before any failure path: calling Environment.Exit while the
        // host is still starting can deadlock against the service lifetime's shutdown wait.
        await Task.Yield();

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            // Letting this escape would trigger BackgroundServiceExceptionBehavior.StopHost,
            // which exits cleanly, so the Service Control Manager never runs its recovery
            // actions. A non-zero exit code is reported as a failure instead.
            _logger.LogCritical(ex, "relay listener cannot start");
            Environment.Exit(1);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var relayNamespace = RequireConfig("RELAY_NAMESPACE");
        var hcName = RequireConfig("RELAY_HC_NAME");
        var listenKeyName = RequireConfig("RELAY_LISTEN_KEYNAME");
        var listenKey = RequireConfig("RELAY_LISTEN_KEY");
        var hermesTargetUrl = _configuration["HERMES_TARGET_URL"] ?? "http://localhost:3978/api/messages";
        var timeoutSeconds = int.TryParse(_configuration["REQUEST_TIMEOUT_SECONDS"], out var t) ? t : 30;
        var maxRetrySeconds = int.TryParse(_configuration["RELAY_RETRY_MAX_SECONDS"], out var m) && m > 0 ? m : 60;
        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var endpoint = new Uri($"sb://{relayNamespace}/{hcName}");
        var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(listenKeyName, listenKey);

        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(maxRetrySeconds);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            attempt++;

            // HybridConnectionListener.OpenAsync marks the instance read-only before it
            // connects, so a failed instance cannot be reopened; build a fresh one per attempt.
            var listener = CreateListener(endpoint, tokenProvider, hermesTargetUrl);
            try
            {
                await listener.OpenAsync(stoppingToken);
                _listener = listener;
                break;
            }
            catch (Exception ex) when (IsTransient(ex) && !stoppingToken.IsCancellationRequested)
            {
                await CloseQuietlyAsync(listener);
                _logger.LogWarning(ex, "relay open attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
            }
            catch
            {
                await CloseQuietlyAsync(listener);
                throw;
            }
        }

        _logger.LogInformation(
            "listening on hybrid connection '{HcName}' @ {Namespace}, forwarding to {Target} (attempts: {Attempts}, elapsed: {Elapsed})",
            hcName, relayNamespace, hermesTargetUrl, attempt, elapsed.Elapsed);

        // Waits here for the service/process lifetime; stoppingToken fires
        // on Ctrl+C in console mode or a Windows Service stop request.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private HybridConnectionListener CreateListener(Uri endpoint, TokenProvider tokenProvider, string hermesTargetUrl)
    {
        var listener = new HybridConnectionListener(endpoint, tokenProvider);
        listener.Connecting += (_, _) => _logger.LogInformation("connecting to Azure Relay...");
        listener.Offline += (_, _) => _logger.LogWarning("offline — control channel dropped, retrying...");
        listener.Online += (_, _) => _logger.LogInformation("online — control channel established.");
        listener.RequestHandler = context => ForwardAsync(context, hermesTargetUrl);
        return listener;
    }

    // Auth and endpoint errors are config mistakes; retrying them forever would hide them.
    private static bool IsTransient(Exception ex) => ex switch
    {
        AuthorizationFailedException => false,
        EndpointNotFoundException => false,
        RelayException => true,
        TimeoutException => true,
        HttpRequestException => true,
        System.Net.Sockets.SocketException => true,
        System.Net.WebSockets.WebSocketException => true,
        _ => false,
    };

    private async Task CloseQuietlyAsync(HybridConnectionListener listener)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await listener.CloseAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ignoring error while closing a failed relay listener");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            _logger.LogInformation("shutting down, closing relay listener...");
            try
            {
                await _listener.CloseAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "error closing relay listener during shutdown");
            }
        }
        await base.StopAsync(cancellationToken);
    }

    private async void ForwardAsync(RelayedHttpListenerContext context, string hermesTargetUrl)
    {
        try
        {
            using var forwardRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), hermesTargetUrl);

            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is null || SkipRequestHeaders.Contains(key))
                {
                    continue;
                }
                forwardRequest.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]);
            }

            if (context.Request.HasEntityBody)
            {
                using var ms = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(ms);
                var bodyBytes = ms.ToArray();
                forwardRequest.Content = new ByteArrayContent(bodyBytes);
                var contentType = context.Request.Headers["Content-Type"];
                if (!string.IsNullOrEmpty(contentType))
                {
                    forwardRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            using var hermesResponse = await httpClient.SendAsync(forwardRequest);
            var responseBytes = await hermesResponse.Content.ReadAsByteArrayAsync();

            context.Response.StatusCode = hermesResponse.StatusCode;
            context.Response.StatusDescription = hermesResponse.ReasonPhrase ?? string.Empty;
            if (responseBytes.Length > 0)
            {
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error forwarding relayed request to Hermes");
            try
            {
                context.Response.StatusCode = HttpStatusCode.BadGateway;
                context.Response.StatusDescription = "Relay listener failed to reach Hermes";
            }
            catch
            {
                // context may already be in a bad state; nothing more we can do
            }
        }
        finally
        {
            // MUST close the context or the relay leaks the connection slot.
            context.Response.Close();
        }
    }

    private string RequireConfig(string name)
    {
        var value = _configuration[name];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"Required setting '{name}' is not configured (checked appsettings.json and environment variables).")
            : value;
    }
}
