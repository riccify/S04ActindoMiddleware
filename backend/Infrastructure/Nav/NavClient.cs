using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Application.Services;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Infrastructure.Nav;

public sealed class NavClient : INavClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsStore _settingsStore;
    private readonly ProductJobQueue _productJobQueue;
    private readonly ILogger<NavClient> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public NavClient(
        HttpClient httpClient,
        ISettingsStore settingsStore,
        ProductJobQueue productJobQueue,
        ILogger<NavClient> logger)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _productJobQueue = productJobQueue;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(settings.NavApiUrl) &&
               !string.IsNullOrWhiteSpace(settings.NavApiToken);
    }

    public async Task<IReadOnlyList<NavCustomerRecord>> GetCustomersAsync(CancellationToken cancellationToken = default)
    {
        var payload = new { requestType = "actindo.customer.id.get" };
        var response = await PostAsync(payload, cancellationToken);

        var customers = new List<NavCustomerRecord>();

        if (response.TryGetProperty("customers", out var customersElement) &&
            customersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in customersElement.EnumerateArray())
            {
                var navId = item.GetProperty("nav_id").GetInt32();
                int? actindoId = null;

                if (item.TryGetProperty("actindo_id", out var actindoIdElement) &&
                    actindoIdElement.ValueKind == JsonValueKind.Number)
                {
                    actindoId = actindoIdElement.GetInt32();
                }

                customers.Add(new NavCustomerRecord(navId, actindoId));
            }
        }

        _logger.LogInformation("Retrieved {Count} customers from NAV API", customers.Count);
        return customers;
    }

    public async Task<IReadOnlyList<NavProductRecord>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        // Use actindo.product.ids.get (plural) to get products WITH variants
        var payload = new { requestType = "actindo.product.ids.get" };
        var response = await PostAsync(payload, cancellationToken);

        var products = new List<NavProductRecord>();

        if (response.TryGetProperty("products", out var productsElement) &&
            productsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in productsElement.EnumerateArray())
            {
                var navId = GetStringValue(item, "nav_id");
                var actindoId = GetStringValue(item, "actindo_id");
                var name = GetStringValue(item, "name");

                // Parse variants if present
                var variants = new List<NavProductVariant>();
                if (item.TryGetProperty("variants", out var variantsElement) &&
                    variantsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var variant in variantsElement.EnumerateArray())
                    {
                        var varNavId = GetStringValue(variant, "nav_id");
                        var varActindoId = GetStringValue(variant, "actindo_id");
                        var varName = GetStringValue(variant, "name");

                        if (!string.IsNullOrEmpty(varNavId))
                        {
                            variants.Add(new NavProductVariant(varNavId, varActindoId, varName ?? string.Empty));
                        }
                    }
                }

                if (!string.IsNullOrEmpty(navId))
                {
                    products.Add(new NavProductRecord
                    {
                        NavId = navId,
                        Sku = navId, // NAV uses nav_id as SKU
                        ActindoId = actindoId,
                        Name = name ?? string.Empty,
                        Variants = variants
                    });
                }
            }
        }

        _logger.LogInformation("Retrieved {Count} products from NAV API", products.Count);
        return products;
    }

    public async Task SetCustomerActindoIdAsync(int navId, int actindoId, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            requestType = "actindo.customer.id.set",
            customer = new { nav_id = navId, actindo_id = actindoId }
        };

        await PostAsync(payload, cancellationToken);
        _logger.LogInformation("Set Actindo ID {ActindoId} for NAV customer {NavId}", actindoId, navId);
    }

    public async Task SetProductActindoIdsAsync(
        IEnumerable<NavProductSyncRequest> products,
        CancellationToken cancellationToken = default)
    {
        var productsList = products.Select(p =>
        {
            if (p.Variants != null && p.Variants.Count > 0)
            {
                return new
                {
                    nav_id = p.NavId,
                    actindo_id = p.ActindoId,
                    variants = p.Variants.Select(v => new
                    {
                        nav_id = v.NavId,
                        actindo_id = v.ActindoId
                    }).ToArray()
                };
            }

            return (object)new
            {
                nav_id = p.NavId,
                actindo_id = p.ActindoId
            };
        }).ToList();

        if (productsList.Count == 0)
            return;

        var payload = new
        {
            requestType = "actindo.product.id.set",
            products = productsList
        };

        await PostAsync(payload, cancellationToken);
        _logger.LogInformation("Set Actindo IDs for {Count} products in NAV", productsList.Count);
    }

    public async Task ClearProductActindoIdsAsync(
        IEnumerable<NavProductClearRequest> products,
        CancellationToken cancellationToken = default)
    {
        var productList = products.ToList();
        if (productList.Count == 0)
            return;

        // NAV expects one ids.del request per nav_id
        var navIds = new List<string>();
        foreach (var p in productList)
        {
            navIds.Add(p.NavId);
            if (p.VariantNavIds != null)
                navIds.AddRange(p.VariantNavIds);
        }

        foreach (var navId in navIds)
        {
            var payload = new { requestType = "actindo.product.ids.del", nav_id = navId };
            await PostAsync(payload, cancellationToken);
        }

        _logger.LogInformation("Cleared Actindo IDs for {Count} nav_ids in NAV", navIds.Count);
    }

    private async Task<JsonElement> PostAsync(object payload, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.NavApiUrl))
            throw new InvalidOperationException("NAV API URL is not configured");

        if (string.IsNullOrWhiteSpace(settings.NavApiToken))
            throw new InvalidOperationException("NAV API Token is not configured");

        var serializedPayload = JsonSerializer.Serialize(payload, SerializerOptions);
        var tokenPreview = settings.NavApiToken.Length > 8
            ? settings.NavApiToken[..8] + "..."
            : "(short)";
        _logger.LogInformation(
            "NAV API POST {Url} | Token starts with: {TokenPreview} | Payload: {Payload}",
            settings.NavApiUrl,
            tokenPreview,
            serializedPayload);

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.NavApiToken);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(settings.NavApiUrl, payload, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "NAV API response {StatusCode}: {Response}",
                (int)response.StatusCode,
                responseContent);

            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement.Clone();

            // NAV returns HTTP 200 even for errors — check body for success: false
            if (root.TryGetProperty("success", out var successProp) &&
                successProp.ValueKind == JsonValueKind.False)
            {
                var errorMsg = root.TryGetProperty("error", out var errProp)
                    ? errProp.GetString()
                    : null;
                _logger.LogWarning(
                    "NAV API returned success=false for {Url}. Error={Error}",
                    settings.NavApiUrl,
                    errorMsg ?? "(none)");
                AppendJobLog(settings.NavApiUrl, false, errorMsg ?? "NAV API meldet Fehler (success: false)", serializedPayload, responseContent);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(errorMsg) ? "NAV API meldet Fehler (success: false)" : errorMsg);
            }

            AppendJobLog(settings.NavApiUrl, true, requestPayload: serializedPayload, responsePayload: responseContent);
            return root;
        }
        catch (Exception ex)
        {
            AppendJobLog("(nav-api)", false, ex.Message, serializedPayload);
            _logger.LogError(ex, "NAV API request failed");
            throw;
        }
    }

    private static string? GetStringValue(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private void AppendJobLog(string endpoint, bool success, string? error = null, string? requestPayload = null, string? responsePayload = null)
    {
        var jobId = ProductJobQueue.CurrentJobId;
        if (jobId.HasValue)
            _productJobQueue.AddLog(jobId.Value, endpoint, success, error, requestPayload, responsePayload);
    }
}
