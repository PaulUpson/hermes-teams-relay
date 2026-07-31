using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Logging;

namespace HermesTeamsRelayFunction;

/// <summary>
/// The public HTTPS endpoint registered with Bot Framework as the Teams bot's
/// messaging endpoint (https://&lt;app&gt;.azurewebsites.net/api/messages).
///
/// It does no bot logic itself: it forwards the raw request through an Azure
/// Relay Hybrid Connection to the on-prem listener sitting next to Hermes,
/// and relays the response back. This is the piece that replaces the dev
/// tunnel — Teams/Bot Framework only ever talks to this Azure-hosted Function,
/// never to anything inside the corporate network directly.
/// </summary>
public class RelayForwarderFunction
{
    private static readonly HttpClient httpClient = new();

    // Headers HttpRequestMessage/HttpClient manage themselves; copying these
    // verbatim from the inbound request causes conflicts or is simply wrong
    // for a new outbound hop.
    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Content-Type"
    };

    private readonly ILogger<RelayForwarderFunction> _logger;

    public RelayForwarderFunction(ILogger<RelayForwarderFunction> logger)
    {
        _logger = logger;
    }

    [Function("messages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "messages")] HttpRequestData req)
    {
        var relayNamespace = RequireSetting("RELAY_NAMESPACE");
        var hcName = RequireSetting("RELAY_HC_NAME");
        var sendKeyName = RequireSetting("RELAY_SEND_KEYNAME");
        var sendKey = RequireSetting("RELAY_SEND_KEY");

        var relayUri = new Uri($"https://{relayNamespace}/{hcName}");

        try
        {
            var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(sendKeyName, sendKey);
            var token = (await tokenProvider.GetTokenAsync(relayUri.AbsoluteUri, TimeSpan.FromMinutes(5))).TokenString;

            using var forwardRequest = new HttpRequestMessage(HttpMethod.Post, relayUri);

            // Relay-hop auth. Separate header from "Authorization" by design,
            // so it never collides with the Bot Framework JWT below.
            forwardRequest.Headers.TryAddWithoutValidation("ServiceBusAuthorization", token);

            // Pass every inbound header straight through, EXCEPT the ones
            // HttpRequestMessage manages itself. This preserves the original
            // Bot Framework "Authorization" JWT byte-for-byte — Hermes's Teams
            // adapter validates that JWT itself once the request reaches it;
            // this relay hop does not touch it.
            foreach (var header in req.Headers)
            {
                if (SkipRequestHeaders.Contains(header.Key))
                {
                    continue;
                }
                forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var bodyBytes = await ReadAllBytesAsync(req.Body);
            if (bodyBytes.Length > 0)
            {
                forwardRequest.Content = new ByteArrayContent(bodyBytes);
                if (req.Headers.TryGetValues("Content-Type", out var contentTypeValues))
                {
                    forwardRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentTypeValues);
                }
            }

            if (bodyBytes.Length > 60_000)
            {
                _logger.LogWarning(
                    "Outbound payload is {SizeBytes} bytes, close to Azure Relay's ~64KB control-channel " +
                    "limit per request. Large Adaptive Card attachments may be rejected on this hop.",
                    bodyBytes.Length);
            }

            using var relayResponse = await httpClient.SendAsync(forwardRequest);
            var responseBytes = await relayResponse.Content.ReadAsByteArrayAsync();

            var response = req.CreateResponse(relayResponse.StatusCode);
            foreach (var header in relayResponse.Content.Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (responseBytes.Length > 0)
            {
                await response.Body.WriteAsync(responseBytes);
            }
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relay Teams message to the on-prem Hermes listener via Azure Relay.");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errorResponse.WriteStringAsync("Failed to reach the on-prem Hermes relay listener.");
            return errorResponse;
        }
    }

    private static string RequireSetting(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} app setting is not configured on the Function App.");

    private static async Task<byte[]> ReadAllBytesAsync(Stream body)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
