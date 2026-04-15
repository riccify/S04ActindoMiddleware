using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Infrastructure.Actindo;

/// <summary>
/// Internal record to hold parsed product data from Actindo.
/// </summary>
internal sealed record ActindoProductData(
    int? Id,
    string Sku,
    string VariantStatus,
    string? ParentSku,
    string? VariantCode,
    string? CreatedAt,
    string? Name);

/// <summary>
/// Record for simplified sync comparison - just ID and SKU.
/// </summary>
public sealed record ActindoSyncProduct(
    int Id,
    string Sku,
    string Name,
    string VariantStatus);

/// <summary>
/// Record for detailed product info including variants.
/// </summary>
public sealed record ActindoProductDetails
{
    public required int Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required string VariantStatus { get; init; }
    public string? VariantCode { get; init; }
    public IReadOnlyList<int> ChildrenIds { get; init; } = Array.Empty<int>();
    public decimal? Price { get; init; }
}

public sealed class ActindoProductListService
{
    private const int ProductListPageSize = 250;
    private const int VariantsListBatchSize = 100;
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationService _authService;
    private readonly IActindoEndpointProvider _endpoints;
    private readonly ILogger<ActindoProductListService> _logger;

    public ActindoProductListService(
        HttpClient httpClient,
        IAuthenticationService authService,
        IActindoEndpointProvider endpoints,
        ILogger<ActindoProductListService> logger)
    {
        _httpClient = httpClient;
        _authService = authService;
        _endpoints = endpoints;
        _logger = logger;
    }

    /// <summary>
    /// Gets a simple list of all products in Actindo for sync comparison.
    /// Returns ID, SKU, Name, and VariantStatus.
    /// </summary>
    public async Task<IReadOnlyList<ActindoSyncProduct>> GetAllProductsForSyncAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await FetchProductElementsAsync(cancellationToken);

        return items
            .Where(p => p.Id.HasValue)
            .Select(p => new ActindoSyncProduct(
                p.Id!.Value,
                p.Sku,
                p.Name ?? string.Empty,
                p.VariantStatus))
            .ToList();
    }

    /// <summary>
    /// Gets detailed product info including variant children IDs.
    /// </summary>
    public async Task<ActindoProductDetails?> GetProductDetailsAsync(
        int actindoId,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await _endpoints.GetAsync(cancellationToken);
        var endpoint = endpoints.GetProduct;
        var token = await _authService.GetValidAccessTokenAsync(cancellationToken);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new { product = new { id = actindoId } };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Actindo get product {Id} failed {Status}: {Content}",
                    actindoId, (int)response.StatusCode, content);
                return null;
            }

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            var id = TryReadInt(data, "id") ?? actindoId;
            var sku = data.TryGetProperty("sku", out var skuProp) ? skuProp.GetString() ?? string.Empty : string.Empty;
            var variantStatus = data.TryGetProperty("variantStatus", out var vsProp) ? vsProp.GetString() ?? "single" : "single";

            // Get variant code for child products
            var variantCode = data.TryGetProperty("_pim_varcode", out var vcProp) ? vcProp.GetString() : null;

            // Get name from various possible fields
            var name = GetFirstNonEmpty(data,
                "_pim_art_name__actindo_basic__de_DE",
                "_pim_art_name__actindo_basic__en_US");

            // Get children IDs from _pim_variants
            var childrenIds = new List<int>();
            if (data.TryGetProperty("_pim_variants", out var variants) &&
                variants.ValueKind == JsonValueKind.Object &&
                variants.TryGetProperty("childrenIds", out var childrenObj) &&
                childrenObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in childrenObj.EnumerateObject())
                {
                    if (child.Value.TryGetInt32(out var childId))
                    {
                        childrenIds.Add(childId);
                    }
                }
            }

            // Get price
            decimal? price = null;
            if (data.TryGetProperty("_pim_price", out var priceData) &&
                priceData.ValueKind == JsonValueKind.Object &&
                priceData.TryGetProperty("currencies", out var currencies) &&
                currencies.ValueKind == JsonValueKind.Array)
            {
                foreach (var currency in currencies.EnumerateArray())
                {
                    if (currency.TryGetProperty("basePrice", out var basePrice) &&
                        basePrice.TryGetProperty("price", out var priceProp))
                    {
                        if (priceProp.TryGetDecimal(out var p))
                            price = p;
                        else if (priceProp.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(priceProp.GetString(), out var parsed))
                            price = parsed;
                        break;
                    }
                }
            }

            return new ActindoProductDetails
            {
                Id = id,
                Sku = sku,
                Name = name,
                VariantStatus = variantStatus,
                VariantCode = variantCode,
                ChildrenIds = childrenIds,
                Price = price
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get product details for {ActindoId}", actindoId);
            return null;
        }
    }

    /// <summary>
    /// Holt Produkte aus Actindo.
    /// </summary>
    /// <param name="includeVariants">Wenn true, werden auch Child-Varianten als separate Produkte zurückgegeben</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<IReadOnlyList<ProductListItem>> GetActindoProductsAsync(
        bool includeVariants = false,
        CancellationToken cancellationToken = default)
    {
        var items = await FetchProductElementsAsync(cancellationToken);

        // Build lookup for variant counts und parent SKUs
        var childrenBySku = items
            .Where(p => p.VariantStatus == "child")
            .ToArray();

        int CountVariants(string masterSku) =>
            string.IsNullOrWhiteSpace(masterSku)
                ? 0
                : childrenBySku.Count(c => c.ParentSku == masterSku ||
                    c.Sku.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase));

        var result = new List<ProductListItem>();

        foreach (var item in items)
        {
            // Wenn includeVariants=false, nur single und master zeigen
            if (!includeVariants && item.VariantStatus == "child")
                continue;

            DateTimeOffset? created = null;
            if (DateTimeOffset.TryParse(item.CreatedAt, out var parsed))
            {
                created = parsed;
            }

            var variantCount = item.VariantStatus == "master" ? CountVariants(item.Sku) : (int?)null;

            result.Add(new ProductListItem
            {
                JobId = Guid.Empty,
                ProductId = item.Id,
                Sku = item.Sku,
                Name = item.Name ?? string.Empty,
                VariantCount = variantCount,
                CreatedAt = created,
                VariantStatus = item.VariantStatus,
                ParentSku = item.ParentSku,
                VariantCode = item.VariantCode
            });
        }

        return result;
    }

    /// <summary>
    /// Holt alle Varianten für ein Master-Produkt.
    /// </summary>
    public async Task<IReadOnlyList<ProductListItem>> GetVariantsForMasterAsync(
        string masterSku,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetVariantsForMasterAsync called for masterSku={MasterSku}", masterSku);

        if (string.IsNullOrWhiteSpace(masterSku))
            return Array.Empty<ProductListItem>();

        var items = await FetchProductElementsAsync(cancellationToken);
        var result = new List<ProductListItem>();

        // Log all children for debugging
        var allChildren = items.Where(p => p.VariantStatus == "child").ToList();
        _logger.LogInformation("Found {Count} total child products", allChildren.Count);

        foreach (var item in items)
        {
            if (item.VariantStatus != "child")
                continue;

            // Match by parentSku or by SKU prefix
            var isVariant = item.ParentSku == masterSku ||
                item.Sku.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("Checking child: sku={Sku}, parentSku={ParentSku}, masterSku={MasterSku}, isMatch={IsMatch}",
                item.Sku, item.ParentSku, masterSku, isVariant);

            if (!isVariant)
                continue;

            DateTimeOffset? created = null;
            if (DateTimeOffset.TryParse(item.CreatedAt, out var parsed))
            {
                created = parsed;
            }

            result.Add(new ProductListItem
            {
                JobId = Guid.Empty,
                ProductId = item.Id,
                Sku = item.Sku,
                Name = item.Name ?? string.Empty,
                VariantCount = null,
                CreatedAt = created,
                VariantStatus = "child",
                ParentSku = item.ParentSku ?? masterSku,
                VariantCode = item.VariantCode
            });
        }

        _logger.LogInformation("Returning {Count} variants for masterSku={MasterSku}", result.Count, masterSku);
        return result;
    }

    public async Task<IReadOnlyList<int>> GetVariantIdsForMasterAsync(
        string masterSku,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(masterSku))
            return Array.Empty<int>();

        var items = await FetchProductElementsAsync(cancellationToken);
        var prefix = masterSku + "-";

        var ids = items
            .Where(p => p.VariantStatus == "child")
            .Where(p => p.Sku.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Id.HasValue)
            .Select(p => p.Id!.Value)
            .Distinct()
            .ToList();

        return ids;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetVariantChildrenByMasterIdsAsync(
        IEnumerable<int> masterIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(masterIds);

        var distinctIds = masterIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (distinctIds.Length == 0)
            return new Dictionary<int, IReadOnlyList<int>>();

        var endpoints = await _endpoints.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.GetVariantsList))
        {
            _logger.LogWarning("GetVariantsList endpoint is not configured.");
            return new Dictionary<int, IReadOnlyList<int>>();
        }

        var token = await _authService.GetValidAccessTokenAsync(cancellationToken);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var result = new Dictionary<int, IReadOnlyList<int>>();

        foreach (var batch in Chunk(distinctIds, VariantsListBatchSize))
        {
            var payload = new { ids = batch };
            using var response = await _httpClient.PostAsJsonAsync(endpoints.GetVariantsList, payload, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Actindo variants list failed {Status}: {Content}", (int)response.StatusCode, content);
                throw new InvalidOperationException($"Actindo variants list failed ({(int)response.StatusCode}): {content}");
            }

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in data.EnumerateArray())
            {
                var masterId = TryReadInt(item, "id");
                if (!masterId.HasValue)
                    continue;

                result[masterId.Value] = ReadChildrenIds(item);
            }
        }

        return result;
    }

    private async Task<List<ActindoProductData>> FetchProductElementsAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _endpoints.GetAsync(cancellationToken);
        var endpoint = endpoints.GetProductList;
        var token = await _authService.GetValidAccessTokenAsync(cancellationToken);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var result = new List<ActindoProductData>();
        var seenProductKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;

        while (true)
        {
            var payload = new
            {
                start,
                limit = ProductListPageSize
            };

            // Actindo uses POST for getList (not GET)
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Actindo product list failed {Status}: {Content}", (int)response.StatusCode, content);
                throw new InvalidOperationException($"Actindo product list failed ({(int)response.StatusCode}): {content}");
            }

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            var pageCount = 0;

            foreach (var item in data.EnumerateArray())
            {
                pageCount++;

                var id = TryReadInt(item, "id") ?? TryReadInt(item, "entityId");
                var sku = item.TryGetProperty("sku", out var skuProp) ? skuProp.GetString() ?? string.Empty : string.Empty;
                var variantStatus = item.TryGetProperty("variantStatus", out var vsProp) ? vsProp.GetString() ?? "single" : "single";
                var parentSku = item.TryGetProperty("_pim_parent_sku", out var psProp) ? psProp.GetString() : null;
                var variantCode = item.TryGetProperty("_pim_varcode", out var vcProp) ? vcProp.GetString() : null;
                var createdAt = item.TryGetProperty("created", out var createdProp) ? createdProp.GetString() : null;

                // Name might not be in the list response, but we'll try
                var name = GetFirstNonEmpty(item,
                    "_pim_art_name__actindo_basic__de_DE",
                    "_pim_art_name__actindo_basic__en_US");

                var productKey = id.HasValue
                    ? $"id:{id.Value}"
                    : $"sku:{sku}";

                if (!seenProductKeys.Add(productKey))
                    continue;

                result.Add(new ActindoProductData(id, sku, variantStatus, parentSku, variantCode, createdAt, name));
            }

            if (pageCount < ProductListPageSize)
                break;

            start += ProductListPageSize;
        }

        _logger.LogInformation("Fetched {Count} products from Actindo", result.Count);

        return result;
    }

    private static int? TryReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
            return n;

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static IReadOnlyList<int> ReadChildrenIds(JsonElement element)
    {
        if (!element.TryGetProperty("_pim_variants", out var variants) ||
            variants.ValueKind != JsonValueKind.Object ||
            !variants.TryGetProperty("childrenIds", out var childrenObj) ||
            childrenObj.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<int>();
        }

        var childrenIds = new List<int>();
        foreach (var child in childrenObj.EnumerateObject())
        {
            if (child.Value.TryGetInt32(out var childId))
            {
                childrenIds.Add(childId);
            }
        }

        return childrenIds;
    }

    private static IEnumerable<int[]> Chunk(IReadOnlyList<int> source, int size)
    {
        for (var index = 0; index < source.Count; index += size)
        {
            var length = Math.Min(size, source.Count - index);
            var batch = new int[length];
            for (var i = 0; i < length; i++)
            {
                batch[i] = source[index + i];
            }

            yield return batch;
        }
    }

    private static string GetFirstNonEmpty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return string.Empty;
    }
}
