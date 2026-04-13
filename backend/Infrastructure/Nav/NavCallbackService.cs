using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Application.Services;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Infrastructure.Nav;

public sealed class NavCallbackService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsStore _settingsStore;
    private readonly ProductJobQueue _productJobQueue;
    private readonly ILogger<NavCallbackService> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public NavCallbackService(
        HttpClient httpClient,
        ISettingsStore settingsStore,
        ProductJobQueue productJobQueue,
        ILogger<NavCallbackService> logger)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _productJobQueue = productJobQueue;
        _logger = logger;
    }

    /// <summary>
    /// Sendet das Sync-Ergebnis an NAV zurück. Wirft keine Exception — Fehler werden nur geloggt.
    /// Gibt true zurück wenn NAV mit {"success": true} geantwortet hat.
    /// </summary>
    public async Task<bool> SendCallbackAsync(
        string sku,
        string? bufferId,
        object result,
        bool created,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(settings.NavApiUrl) || string.IsNullOrWhiteSpace(settings.NavApiToken))
            {
                _logger.LogWarning("NAV callback skipped: NavApiUrl or NavApiToken not configured");
                return false;
            }

            // Serialize result to JsonElement, then merge with sku + bufferId + created
            var resultJson = JsonSerializer.SerializeToElement(result, SerializerOptions);
            var payload = BuildPayload(resultJson, sku, bufferId, created);

            var tokenPreview = settings.NavApiToken!.Length > 8
                ? settings.NavApiToken[..8] + "..."
                : "(short)";
            var payloadJson = JsonSerializer.Serialize(payload, SerializerOptions);
            _logger.LogInformation(
                "NAV callback: POST {Url} | SKU={Sku} BufferId={BufferId} Created={Created} | Token starts with: {TokenPreview}",
                settings.NavApiUrl, sku, bufferId ?? "(none)", created, tokenPreview);
            _logger.LogDebug("NAV callback body: {Body}", payloadJson);

            using var request = new HttpRequestMessage(HttpMethod.Post, settings.NavApiUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.NavApiToken);
            request.Content = JsonContent.Create(payload, options: SerializerOptions);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var acknowledged = NavAcknowledgedSuccess(responseBody);
            AppendJobLog(settings.NavApiUrl, response.IsSuccessStatusCode && acknowledged, acknowledged ? null : "NAV callback not acknowledged", payloadJson, responseBody);

            _logger.LogInformation(
                "NAV callback sent for SKU={Sku} BufferId={BufferId}: HTTP {Status} | Acknowledged={Acknowledged} | Response: {Body}",
                sku, bufferId ?? "(none)", (int)response.StatusCode,
                acknowledged,
                responseBody.Length > 500 ? responseBody[..500] : responseBody);

            if (!acknowledged)
            {
                _logger.LogWarning(
                    "NAV callback for SKU={Sku} BufferId={BufferId} was not acknowledged as success. Check URL/token/requestType handling on NAV side.",
                    sku,
                    bufferId ?? "(none)");
            }

            return acknowledged;
        }
        catch (Exception ex)
        {
            AppendJobLog("(nav-callback)", false, ex.Message);
            _logger.LogError(ex,
                "NAV callback failed for SKU={Sku} BufferId={BufferId}",
                sku, bufferId ?? "(none)");
            return false;
        }
    }

    /// <summary>
    /// Prüft ob NAV mit {"success": true} (oder "true") geantwortet hat.
    /// </summary>
    private static bool NavAcknowledgedSuccess(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("success", out var successProp))
            {
                return successProp.ValueKind == JsonValueKind.True ||
                       (successProp.ValueKind == JsonValueKind.String &&
                        successProp.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
            }
        }
        catch { /* kein gültiges JSON */ }
        return false;
    }

    private static object BuildPayload(JsonElement result, string sku, string? bufferId, bool created)
    {
        // Wandelt das result-Objekt in ein Dictionary um und fügt sku + bufferId + created hinzu
        var dict = new System.Collections.Generic.Dictionary<string, object?>();

        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in result.EnumerateObject())
                dict[prop.Name] = prop.Value;
        }

        dict["requestType"] = "actindo.product.response";
        dict["sku"] = sku;
        dict["bufferId"] = bufferId;
        dict["created"] = created;

        return dict;
    }

    private void AppendJobLog(string endpoint, bool success, string? error = null, string? requestPayload = null, string? responsePayload = null)
    {
        var jobId = ProductJobQueue.CurrentJobId;
        if (jobId.HasValue)
            _productJobQueue.AddLog(jobId.Value, endpoint, success, error, requestPayload, responsePayload);
    }
}
