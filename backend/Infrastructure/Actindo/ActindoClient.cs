using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Infrastructure.Actindo;

public sealed class ActindoClient
{
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationService _authenticationService;
    private readonly IActindoAvailabilityTracker _availabilityTracker;
    private readonly ProductJobQueue _productJobQueue;
    private readonly ILogger<ActindoClient> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public ActindoClient(
        HttpClient httpClient,
        IAuthenticationService authenticationService,
        IActindoAvailabilityTracker availabilityTracker,
        ProductJobQueue productJobQueue,
        ILogger<ActindoClient> logger)
    {
        _httpClient = httpClient;
        _authenticationService = authenticationService;
        _availabilityTracker = availabilityTracker;
        _productJobQueue = productJobQueue;
        _logger = logger;
    }

    public async Task<JsonElement> PostAsync(
        string endpoint,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(payload);

        var serializedPayload = JsonSerializer.Serialize(payload, SerializerOptions);
        _logger.LogInformation("Actindo POST {Endpoint} payload: {Payload}", endpoint, serializedPayload);

        var token = await _authenticationService.GetValidAccessTokenAsync(cancellationToken);

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "Actindo response {StatusCode} for {Endpoint}: {Response}",
                (int)response.StatusCode,
                endpoint,
                responseContent);

            if (!response.IsSuccessStatusCode)
            {
                var actindoError = TryExtractActindoErrorMessage(responseContent) ?? responseContent;
                var ex = new InvalidOperationException(actindoError);

                _logger.LogError("Actindo request to {Endpoint} failed with {StatusCode}: {Response}", endpoint, (int)response.StatusCode, responseContent);
                _availabilityTracker.ReportFailure(ex);
                AppendJobLog(endpoint, false, $"HTTP {(int)response.StatusCode}: {actindoError}", serializedPayload, responseContent);
                throw ex;
            }

            _availabilityTracker.ReportSuccess();
            AppendJobLog(endpoint, true, requestPayload: serializedPayload, responsePayload: responseContent);

            using var document = JsonDocument.Parse(responseContent);
            return document.RootElement.Clone();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _availabilityTracker.ReportFailure(ex);
            _logger.LogError(ex, "Actindo request to {Endpoint} failed.", endpoint);
            AppendJobLog(endpoint, false, ex.Message, requestPayload: serializedPayload);
            throw;
        }
    }

    private void AppendJobLog(string endpoint, bool success, string? error = null, string? requestPayload = null, string? responsePayload = null)
    {
        var jobId = ProductJobQueue.CurrentJobId;
        if (jobId.HasValue)
            _productJobQueue.AddLog(jobId.Value, endpoint, success, error, requestPayload, responsePayload);
    }

    private static string? TryExtractActindoErrorMessage(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                return errorProp.GetString();
        }
        catch { }
        return null;
    }
}
