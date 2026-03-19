using System;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.DTOs.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ActindoMiddleware.Infrastructure.Actindo;
using ActindoMiddleware.Infrastructure.Nav;
using System.Threading;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Requests;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/products")]
[Authorize(Policy = AuthPolicies.Read)]
public sealed class ProductsController : ControllerBase
{
    private readonly IDashboardMetricsService _metricsService;
    private readonly ActindoProductListService _productListService;
    private readonly ActindoClient _actindoClient;
    private readonly IActindoEndpointProvider _endpointProvider;
    private readonly ISettingsStore _settingsStore;
    private readonly INavClient _navClient;

    public ProductsController(
        IDashboardMetricsService metricsService,
        ActindoProductListService productListService,
        ActindoClient actindoClient,
        IActindoEndpointProvider endpointProvider,
        ISettingsStore settingsStore,
        INavClient navClient)
    {
        _metricsService = metricsService;
        _productListService = productListService;
        _actindoClient = actindoClient;
        _endpointProvider = endpointProvider;
        _settingsStore = settingsStore;
        _navClient = navClient;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductListItemDto>>> Get(
        [FromQuery] int limit = 200,
        [FromQuery] bool includeVariants = false,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var items = await _metricsService.GetCreatedProductsAsync(limit, includeVariants, cancellationToken);
        return Ok(items.Select(MapToDto));
    }

    [HttpGet("sync")]
    public async Task<ActionResult<IReadOnlyList<ProductListItemDto>>> Sync(
        [FromQuery] bool includeVariants = false,
        CancellationToken cancellationToken = default)
    {
        var items = await _productListService.GetActindoProductsAsync(includeVariants, cancellationToken);
        return Ok(items.Select(MapToDto));
    }

    /// <summary>
    /// Holt alle Varianten für ein Master-Produkt aus der lokalen Datenbank.
    /// </summary>
    [HttpGet("{masterSku}/variants")]
    public async Task<ActionResult<IReadOnlyList<ProductListItemDto>>> GetVariants(
        string masterSku,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(masterSku))
            return BadRequest("masterSku ist erforderlich.");

        var items = await _metricsService.GetVariantsForMasterAsync(masterSku, cancellationToken);
        return Ok(items.Select(MapToDto));
    }

    /// <summary>
    /// Holt alle Lagerbestände für ein Produkt.
    /// </summary>
    [HttpGet("{sku}/stocks")]
    public async Task<ActionResult<IReadOnlyList<ProductStockItemDto>>> GetStocks(
        string sku,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return BadRequest("sku ist erforderlich.");

        var items = await _metricsService.GetProductStocksAsync(sku, cancellationToken);
        return Ok(items.Select(s => new ProductStockItemDto
        {
            Sku = s.Sku,
            WarehouseId = s.WarehouseId,
            Stock = s.Stock,
            UpdatedAt = s.UpdatedAt
        }));
    }

    /// <summary>
    /// Returns all Actindo products whose actindo_id has not yet been written back to NAV.
    /// </summary>
    [HttpGet("nav-sync-errors")]
    public async Task<ActionResult<NavSyncErrorsDto>> GetNavSyncErrors(CancellationToken cancellationToken)
    {
        var navConfigured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!navConfigured)
            return BadRequest(new { error = "NAV API ist nicht konfiguriert" });

        IReadOnlyList<ActindoSyncProduct> actindoProducts;
        IReadOnlyList<NavProductRecord> navProducts;
        try
        {
            var actindoTask = _productListService.GetAllProductsForSyncAsync(cancellationToken);
            var navTask = _navClient.GetProductsAsync(cancellationToken);
            await Task.WhenAll(actindoTask, navTask);
            actindoProducts = actindoTask.Result;
            navProducts = navTask.Result;
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = "Fehler beim Laden der Produktdaten", details = ex.Message });
        }

        // Build SKU-keyed lookups for NAV masters and variants
        var navBySku = navProducts
            .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Flat lookup for all NAV variant entries (nav_id is the variant SKU)
        var navVariantBySku = navProducts
            .SelectMany(p => p.Variants)
            .GroupBy(v => v.NavId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var children = actindoProducts.Where(p => p.VariantStatus == "child").ToList();
        var mastersAndSingles = actindoProducts.Where(p => p.VariantStatus != "child").ToList();

        var items = new List<NavSyncMissingItem>();

        foreach (var product in mastersAndSingles.OrderBy(p => p.Sku))
        {
            // Determine master/single status vs NAV
            navBySku.TryGetValue(product.Sku, out var navRecord);
            var actindoIdStr = product.Id.ToString();

            string masterStatus;
            string? masterNavActindoId = null;

            if (navRecord == null)
            {
                // Not in NAV at all
                masterStatus = "missing";
            }
            else if (string.IsNullOrEmpty(navRecord.ActindoId))
            {
                // In NAV but actindo_id not written back yet
                masterStatus = "missing";
            }
            else if (!string.Equals(navRecord.ActindoId, actindoIdStr, StringComparison.OrdinalIgnoreCase))
            {
                // In NAV but wrong actindo_id
                masterStatus = "mismatch";
                masterNavActindoId = navRecord.ActindoId;
            }
            else
            {
                masterStatus = "ok";
            }

            // Check variants
            var variantChildren = product.VariantStatus == "master"
                ? children
                    .Where(c => c.Sku.StartsWith(product.Sku + "-", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [];

            var problemVariants = new List<NavSyncMissingVariant>();
            foreach (var child in variantChildren.OrderBy(c => c.Sku))
            {
                navVariantBySku.TryGetValue(child.Sku, out var navVariant);
                var childIdStr = child.Id.ToString();

                if (navVariant == null || string.IsNullOrEmpty(navVariant.ActindoId))
                {
                    problemVariants.Add(new NavSyncMissingVariant
                    {
                        Sku = child.Sku,
                        ActindoId = child.Id,
                        Status = "missing"
                    });
                }
                else if (!string.Equals(navVariant.ActindoId, childIdStr, StringComparison.OrdinalIgnoreCase))
                {
                    problemVariants.Add(new NavSyncMissingVariant
                    {
                        Sku = child.Sku,
                        ActindoId = child.Id,
                        Status = "mismatch",
                        NavActindoId = navVariant.ActindoId
                    });
                }
            }

            if (masterStatus != "ok" || problemVariants.Count > 0)
            {
                items.Add(new NavSyncMissingItem
                {
                    Sku = product.Sku,
                    ActindoId = product.Id,
                    VariantStatus = product.VariantStatus,
                    Status = masterStatus == "ok" ? "ok" : masterStatus,
                    NavActindoId = masterNavActindoId,
                    TotalVariants = variantChildren.Count,
                    MissingVariants = problemVariants
                });
            }
        }

        return Ok(new NavSyncErrorsDto
        {
            TotalInActindo = mastersAndSingles.Count,
            MissingFromNav = items.Count,
            Items = items
        });
    }

    [HttpGet("actindo-base-url")]
    public async Task<IActionResult> GetActindoBaseUrl(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        return Ok(new { actindoBaseUrl = settings.ActindoBaseUrl });
    }

    private static ProductListItemDto MapToDto(ProductListItem p) => new()
    {
        JobId = p.JobId,
        ProductId = p.ProductId,
        Sku = p.Sku ?? string.Empty,
        Name = p.Name ?? string.Empty,
        VariantCount = p.VariantCount,
        CreatedAt = p.CreatedAt,
        VariantStatus = p.VariantStatus,
        ParentSku = p.ParentSku,
        VariantCode = p.VariantCode,
        LastPrice = p.LastPrice,
        LastPriceEmployee = p.LastPriceEmployee,
        LastPriceMember = p.LastPriceMember,
        LastStock = p.LastStock,
        LastWarehouseId = p.LastWarehouseId,
        LastPriceUpdatedAt = p.LastPriceUpdatedAt,
        LastStockUpdatedAt = p.LastStockUpdatedAt
    };

    [HttpPost("delete")]
    [Authorize(Policy = AuthPolicies.Write)]
    public async Task<IActionResult> DeleteProduct(
        [FromBody] DeleteProductRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || request.ProductId <= 0 || request.JobId == Guid.Empty)
            return BadRequest("productId und jobId sind erforderlich.");

        var endpoints = await _endpointProvider.GetAsync(cancellationToken);
        var payload = new { product = new { id = request.ProductId } };
        var variantIds = Array.Empty<int>();
        if (!string.IsNullOrWhiteSpace(request.Sku))
        {
            try
            {
                variantIds = (await _productListService
                        .GetVariantIdsForMasterAsync(request.Sku, cancellationToken))
                    .ToArray();
            }
            catch
            {
                // wenn Variantenliste nicht geladen werden kann, fahren wir fort und loeschen nur den Master
            }
        }

        try
        {
            // erst Varianten loeschen
            foreach (var variantId in variantIds)
            {
                var variantPayload = new { product = new { id = variantId } };
                var variantResponse = await _actindoClient.PostAsync(
                    endpoints.DeleteProduct,
                    variantPayload,
                    cancellationToken);

                if (variantResponse.TryGetProperty("success", out var successChild) &&
                    successChild.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    var displayMessage = variantResponse.TryGetProperty("displayMessage", out var msgChild) ? msgChild.GetString() : null;
                    var messageChild = !string.IsNullOrWhiteSpace(displayMessage)
                        ? displayMessage
                        : $"Actindo Delete meldet Fehler fuer Variante {variantId}.";
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = messageChild, variantId });
                }
            }

            var response = await _actindoClient.PostAsync(
                endpoints.DeleteProduct,
                payload,
                cancellationToken);

            // Wenn Actindo ein success=false zurückgibt, als Fehler behandeln
            if (response.TryGetProperty("success", out var successProp) &&
                successProp.ValueKind == System.Text.Json.JsonValueKind.False)
            {
                var displayMessage = response.TryGetProperty("displayMessage", out var msg) ? msg.GetString() : null;
                var displayTitle = response.TryGetProperty("displayMessageTitle", out var title) ? title.GetString() : null;
                var error = response.TryGetProperty("error", out var err) ? err.GetRawText() : null;
                var message = !string.IsNullOrWhiteSpace(displayMessage)
                    ? displayMessage
                    : "Actindo Delete meldet Fehler.";
                return StatusCode(StatusCodes.Status502BadGateway, new { error = message, title = displayTitle, actindo = error });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }

        return NoContent();
    }
}
