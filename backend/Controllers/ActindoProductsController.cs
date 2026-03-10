using System.Diagnostics;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Infrastructure.Actindo;
using ActindoMiddleware.Infrastructure.Nav;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/actindo/products")]
[Authorize(Policy = AuthPolicies.Write)]
public sealed class ActindoProductsController : ControllerBase
{
    private readonly ProductCreateService _productCreateService;
    private readonly ProductSaveService _productSaveService;
    private readonly IDashboardMetricsService _dashboardMetrics;
    private readonly ActindoClient _actindoClient;
    private readonly IActindoEndpointProvider _endpointProvider;
    private readonly ISettingsStore _settingsStore;
    private readonly ProductJobQueue _jobQueue;
    private readonly NavCallbackService _navCallback;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActindoProductsController> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InventoryLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProductLocks = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxConcurrentInventoryPosts = 8;

    public ActindoProductsController(
        ProductCreateService productCreateService,
        ProductSaveService productSaveService,
        IDashboardMetricsService dashboardMetrics,
        ActindoClient actindoClient,
        IActindoEndpointProvider endpointProvider,
        ISettingsStore settingsStore,
        ProductJobQueue jobQueue,
        NavCallbackService navCallback,
        IServiceScopeFactory scopeFactory,
        ILogger<ActindoProductsController> logger)
    {
        _productCreateService = productCreateService;
        _productSaveService = productSaveService;
        _dashboardMetrics = dashboardMetrics;
        _actindoClient = actindoClient;
        _endpointProvider = endpointProvider;
        _settingsStore = settingsStore;
        _jobQueue = jobQueue;
        _navCallback = navCallback;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet("active-jobs/{jobId:guid}/logs")]
    [Authorize(Policy = AuthPolicies.Read)]
    public IActionResult GetJobLogs(Guid jobId)
    {
        var logs = _jobQueue.GetLogs(jobId);
        if (logs is null)
            return NotFound();
        return Ok(logs);
    }

    [HttpGet("active-jobs")]
    [Authorize(Policy = AuthPolicies.Read)]
    public IActionResult GetActiveJobs() => Ok(_jobQueue.GetAll());

    private async Task<ActionResult?> ValidateNavSettingsForAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.NavApiUrl) || string.IsNullOrWhiteSpace(settings.NavApiToken))
            return BadRequest("NAV API Endpoint und Token müssen in den Einstellungen konfiguriert sein, um asynchrone Verarbeitung (await=false) zu nutzen.");
        return null;
    }

    /// <summary>
    /// Erstellt ein Master-Produkt inkl. Inventar und Varianten in Actindo
    /// </summary>
    [HttpPost("create")]
    [ProducesResponseType(typeof(CreateProductResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CreateProductResponse>> CreateProduct(
        [FromBody] CreateProductRequest request,
        CancellationToken _)
    {
        if (request?.Product == null)
            return BadRequest("Product payload is missing");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;

        if (!request.Await)
        {
            var navError = await ValidateNavSettingsForAsync(cancellationToken);
            if (navError != null) return navError;

            var capturedRequest = request;
            var requestPayload = DashboardPayloadSerializer.Serialize(request);
            var sku = request.Product.sku;

            _jobQueue.Enqueue(sku, "create", request.BufferId, async ct =>
            {
                var productLock = ProductLocks.GetOrAdd(sku, _ => new SemaphoreSlim(1, 1));
                await productLock.WaitAsync(ct);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ProductCreateService>();

                    var jobHandle = await _dashboardMetrics.BeginJobAsync(
                        DashboardMetricType.Product, DashboardJobEndpoints.ProductCreate, requestPayload, ct);
                    using var jobScope = DashboardJobContext.Begin(jobHandle.Id);
                    var sw = Stopwatch.StartNew();
                    bool success = false; string? responsePayload = null; string? errorPayload = null;

                    try
                    {
                        var result = await service.CreateAsync(capturedRequest, ct);
                        var product = capturedRequest.Product;
                        var hasVariants = product.Variants?.Count > 0;

                        await _dashboardMetrics.SaveProductAsync(
                            jobHandle.Id, product.sku, GetProductName(product),
                            result.ProductId, hasVariants ? "master" : "single", null, null, ct);

                        if (result.Variants != null)
                        {
                            foreach (var variantResult in result.Variants)
                            {
                                var variantDto = product.Variants?.FirstOrDefault(v => v.sku == variantResult.Sku);
                                await _dashboardMetrics.SaveProductAsync(
                                    jobHandle.Id, variantResult.Sku,
                                    variantDto != null ? GetProductName(variantDto) : string.Empty,
                                    variantResult.Id, "child", product.sku, variantDto?._pim_varcode, ct);
                            }
                        }

                        success = true;
                        responsePayload = DashboardPayloadSerializer.Serialize(result);
                        await _navCallback.SendCallbackAsync(sku, capturedRequest.BufferId, ToNavCallbackPayload(sku, result, created: true), created: true, ct);
                    }
                    catch (Exception ex)
                    {
                        errorPayload = DashboardPayloadSerializer.SerializeError(ex);
                        var navAck = await _navCallback.SendCallbackAsync(sku, capturedRequest.BufferId,
                            new { success = false, error = ex.Message }, created: true, ct);
                        if (!navAck) throw;
                    }
                    finally
                    {
                        await _dashboardMetrics.CompleteJobAsync(
                            jobHandle, success, sw.Elapsed, responsePayload, errorPayload, ct);
                    }
                }
                finally
                {
                    productLock.Release();
                }
            });

            return Accepted(new { message = "Sync wird ausgeführt", bufferId = request.BufferId });
        }

        var jobHandle2 = await _dashboardMetrics.BeginJobAsync(
            DashboardMetricType.Product,
            DashboardJobEndpoints.ProductCreate,
            DashboardPayloadSerializer.Serialize(request),
            cancellationToken);
        using var jobScope2 = DashboardJobContext.Begin(jobHandle2.Id);
        var stopwatch = Stopwatch.StartNew();
        var success2 = false;
        string? responsePayload2 = null;
        string? errorPayload2 = null;
        var createProductLock = ProductLocks.GetOrAdd(request.Product.sku, _ => new SemaphoreSlim(1, 1));
        await createProductLock.WaitAsync(cancellationToken);
        try
        {
            var result = await _productCreateService.CreateAsync(request, cancellationToken);
            success2 = true;
            responsePayload2 = DashboardPayloadSerializer.Serialize(result);

            var product = request.Product;
            var hasVariants = product.Variants?.Count > 0;
            await _dashboardMetrics.SaveProductAsync(
                jobHandle2.Id, product.sku, GetProductName(product),
                result.ProductId, hasVariants ? "master" : "single", null, null, cancellationToken);

            if (result.Variants != null)
            {
                foreach (var variantResult in result.Variants)
                {
                    var variantDto = product.Variants?.FirstOrDefault(v => v.sku == variantResult.Sku);
                    var variantName = variantDto != null ? GetProductName(variantDto) : string.Empty;
                    await _dashboardMetrics.SaveProductAsync(
                        jobHandle2.Id, variantResult.Sku, variantName,
                        variantResult.Id, "child", product.sku, variantDto?._pim_varcode, cancellationToken);
                }
            }

            return Created(string.Empty, result);
        }
        catch (Exception ex)
        {
            errorPayload2 = DashboardPayloadSerializer.SerializeError(ex);
            throw;
        }
        finally
        {
            createProductLock.Release();
            await _dashboardMetrics.CompleteJobAsync(
                jobHandle2, success2, stopwatch.Elapsed, responsePayload2, errorPayload2, cancellationToken);
        }
    }

    private static string GetProductName(DTOs.ProductDto product)
    {
        return product._pim_art_name__actindo_basic__de_DE
            ?? product._pim_art_name__actindo_basic__en_US
            ?? product._pim_art_nameactindo_basic_de_DE
            ?? product._pim_art_nameactindo_basic_en_US
            ?? string.Empty;
    }

    private static string GetNameFromJsonNode(JsonObject? node)
    {
        if (node is null) return string.Empty;
        return node["_pim_art_name__actindo_basic__de_DE"]?.ToString()
            ?? node["_pim_art_name__actindo_basic__en_US"]?.ToString()
            ?? node["_pim_art_nameactindo_basic_de_DE"]?.ToString()
            ?? node["_pim_art_nameactindo_basic_en_US"]?.ToString()
            ?? string.Empty;
    }

    private static (int? actindoId, string? sku, decimal? price, decimal? priceEmployee, decimal? priceMember) ExtractPriceData(JsonElement element)
    {
        int? actindoId = null;
        string? sku = null;
        decimal? price = null;
        decimal? priceEmployee = null;
        decimal? priceMember = null;

        // Actindo ID extrahieren
        if (element.TryGetProperty("id", out var idProp))
        {
            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt32(out var id))
                actindoId = id;
            else if (idProp.ValueKind == JsonValueKind.String && int.TryParse(idProp.GetString(), out var idStr))
                actindoId = idStr;
        }

        // SKU extrahieren (falls vorhanden)
        if (element.TryGetProperty("sku", out var skuProp) && skuProp.ValueKind == JsonValueKind.String)
            sku = skuProp.GetString();

        // Basispreis extrahieren: _pim_price.currencies.EUR.base.price
        price = ExtractPriceFromField(element, "_pim_price");
        priceEmployee = ExtractPriceFromField(element, "_pim_price_employee");
        priceMember = ExtractPriceFromField(element, "_pim_price_member");

        return (actindoId, sku, price, priceEmployee, priceMember);
    }

    private static decimal? ExtractPriceFromField(JsonElement element, string fieldName)
    {
        if (!element.TryGetProperty(fieldName, out var priceObj))
            return null;

        // Versuche: currencies.EUR.basePrice.price (currencies kann Object oder Array sein)
        if (priceObj.TryGetProperty("currencies", out var currencies))
        {
            if (currencies.ValueKind == JsonValueKind.Object)
            {
                // Object: { "EUR": { "basePrice": { "price": 19.99 } } }
                foreach (var currency in currencies.EnumerateObject())
                {
                    if (currency.Value.TryGetProperty("basePrice", out var baseProp) &&
                        baseProp.TryGetProperty("price", out var priceProp))
                    {
                        if (priceProp.TryGetDecimal(out var p))
                            return p;
                    }
                }
            }
            else if (currencies.ValueKind == JsonValueKind.Array)
            {
                // Array: [{ "currency": "EUR", "basePrice": { "price": 19.99 } }]
                foreach (var currency in currencies.EnumerateArray())
                {
                    if (currency.TryGetProperty("basePrice", out var baseProp) &&
                        baseProp.TryGetProperty("price", out var priceProp))
                    {
                        if (priceProp.TryGetDecimal(out var p))
                            return p;
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractSkuFromResponse(JsonElement response)
    {
        // Actindo Response: {"product":{"id":172441,"sku":"10855",...},"success":true,...}
        if (response.TryGetProperty("product", out var product) &&
            product.TryGetProperty("sku", out var skuProp) &&
            skuProp.ValueKind == JsonValueKind.String)
        {
            return skuProp.GetString();
        }
        return null;
    }

    /// <summary>
    /// Aktualisiert ein bestehendes Produkt und seine Varianten in Actindo.
    /// </summary>
    [HttpPost("save")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CreateProductResponse>> SaveProduct(
        [FromBody] SaveProductRequest request,
        CancellationToken _)
    {
        if (request?.Product == null)
            return BadRequest("Product payload is missing");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;

        if (!request.Await)
        {
            var navError = await ValidateNavSettingsForAsync(cancellationToken);
            if (navError != null) return navError;

            var capturedRequest = request;
            var requestPayload = DashboardPayloadSerializer.Serialize(request);
            var sku = request.Product.sku;

            _jobQueue.Enqueue(sku, "save", request.BufferId, async ct =>
            {
                var productLock = ProductLocks.GetOrAdd(sku, _ => new SemaphoreSlim(1, 1));
                await productLock.WaitAsync(ct);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ProductSaveService>();

                    var jobHandle = await _dashboardMetrics.BeginJobAsync(
                        DashboardMetricType.Product, DashboardJobEndpoints.ProductSave, requestPayload, ct);
                    using var jobScope = DashboardJobContext.Begin(jobHandle.Id);
                    var sw = Stopwatch.StartNew();
                    bool success = false; string? responsePayload = null; string? errorPayload = null;

                    try
                    {
                        var result = await service.SaveAsync(capturedRequest, ct);
                        var product = capturedRequest.Product;
                        var hasVariants = product.Variants?.Count > 0;

                        await _dashboardMetrics.SaveProductAsync(
                            jobHandle.Id, product.sku, GetProductName(product),
                            result.ProductId, hasVariants ? "master" : "single", null, null, ct);

                        if (result.Variants != null)
                        {
                            foreach (var variantResult in result.Variants)
                            {
                                var variantDto = product.Variants?.FirstOrDefault(v => v.sku == variantResult.Sku);
                                await _dashboardMetrics.SaveProductAsync(
                                    jobHandle.Id, variantResult.Sku,
                                    variantDto != null ? GetProductName(variantDto) : string.Empty,
                                    variantResult.Id, "child", product.sku, variantDto?._pim_varcode, ct);
                            }
                        }

                        success = true;
                        responsePayload = DashboardPayloadSerializer.Serialize(result);
                        await _navCallback.SendCallbackAsync(sku, capturedRequest.BufferId, ToNavCallbackPayload(sku, result, created: false), created: false, ct);
                    }
                    catch (Exception ex)
                    {
                        errorPayload = DashboardPayloadSerializer.SerializeError(ex);
                        var navAck = await _navCallback.SendCallbackAsync(sku, capturedRequest.BufferId,
                            new { success = false, error = ex.Message }, created: false, ct);
                        if (!navAck) throw;
                    }
                    finally
                    {
                        await _dashboardMetrics.CompleteJobAsync(
                            jobHandle, success, sw.Elapsed, responsePayload, errorPayload, ct);
                    }
                }
                finally
                {
                    productLock.Release();
                }
            });

            return Accepted(new { message = "Sync wird ausgeführt", bufferId = request.BufferId });
        }

        var jobHandle2 = await _dashboardMetrics.BeginJobAsync(
            DashboardMetricType.Product,
            DashboardJobEndpoints.ProductSave,
            DashboardPayloadSerializer.Serialize(request),
            cancellationToken);
        using var jobScope2 = DashboardJobContext.Begin(jobHandle2.Id);
        var stopwatch = Stopwatch.StartNew();
        var success2 = false;
        string? responsePayload2 = null;
        string? errorPayload2 = null;
        var saveProductLock = ProductLocks.GetOrAdd(request.Product.sku, _ => new SemaphoreSlim(1, 1));
        await saveProductLock.WaitAsync(cancellationToken);
        try
        {
            var result = await _productSaveService.SaveAsync(request, cancellationToken);
            success2 = true;
            responsePayload2 = DashboardPayloadSerializer.Serialize(result);

            var product2 = request.Product;
            var hasVariants2 = product2.Variants?.Count > 0;
            await _dashboardMetrics.SaveProductAsync(
                jobHandle2.Id, product2.sku, GetProductName(product2),
                result.ProductId, hasVariants2 ? "master" : "single", null, null, cancellationToken);

            if (result.Variants != null)
            {
                foreach (var variantResult in result.Variants)
                {
                    var variantDto = product2.Variants?.FirstOrDefault(v => v.sku == variantResult.Sku);
                    await _dashboardMetrics.SaveProductAsync(
                        jobHandle2.Id, variantResult.Sku,
                        variantDto != null ? GetProductName(variantDto) : string.Empty,
                        variantResult.Id, "child", product2.sku, variantDto?._pim_varcode, cancellationToken);
                }
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            errorPayload2 = DashboardPayloadSerializer.SerializeError(ex);
            throw;
        }
        finally
        {
            saveProductLock.Release();
            await _dashboardMetrics.CompleteJobAsync(
                jobHandle2, success2, stopwatch.Elapsed, responsePayload2, errorPayload2, cancellationToken);
        }
    }

    [HttpPost("inventory")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AdjustInventory(
        [FromBody] AdjustInventoryRequest request,
        CancellationToken _)
    {
        if (request?.Inventories == null || request.Inventories.Count == 0)
        {
            return BadRequest("inventories darf nicht leer sein.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;

        var jobHandle = await _dashboardMetrics.BeginJobAsync(
            DashboardMetricType.Product,
            DashboardJobEndpoints.ProductInventory,
            DashboardPayloadSerializer.Serialize(request),
            cancellationToken);
        using var jobScope = DashboardJobContext.Begin(jobHandle.Id);
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        string? responsePayload = null;
        string? errorPayload = null;

        try
        {
            var endpoints = await _endpointProvider.GetAsync(cancellationToken);
            var mappings = (await _settingsStore.GetActindoSettingsAsync(cancellationToken)).WarehouseMappings;
            var results = new ConcurrentBag<InventoryUpdateResultItem>();
            var workItems = new List<(string sku, InventoryStock stock, int warehouseId)>();

            foreach (var kvp in request.Inventories)
            {
                var sku = kvp.Key;
                var entry = kvp.Value;
                if (string.IsNullOrWhiteSpace(sku) || entry?.Stocks == null || entry.Stocks.Count == 0)
                    continue;

                foreach (var stockEntry in entry.Stocks)
                {
                    if (stockEntry?.WarehouseId is null || stockEntry.Stock is null)
                        continue;

                    if (!mappings.TryGetValue(stockEntry.WarehouseId, out var warehouseId))
                    {
                        results.Add(new InventoryUpdateResultItem
                        {
                            Sku = sku,
                            WarehouseId = stockEntry.WarehouseId,
                            Success = false,
                            Error = $"Lager '{stockEntry.WarehouseId}' ist nicht gemappt. Bitte in den Einstellungen unter 'Lager-Konfiguration' eintragen."
                        });
                        continue;
                    }

                    workItems.Add((sku, stockEntry, warehouseId));
                }
            }

            var throttler = new SemaphoreSlim(MaxConcurrentInventoryPosts);
            var tasks = workItems.Select(async item =>
            {
                await throttler.WaitAsync(cancellationToken);
                var key = $"wh:{item.warehouseId}";
                var sem = InventoryLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(cancellationToken);
                try
                {
                    var payload = new
                    {
                        inventory = new
                        {
                            sku = item.sku,
                            synchronousSync = true,
                            compareOldValue = true,
                            _fulfillment_inventory_amount = item.stock.Stock,
                            _fulfillment_inventory_warehouse = item.warehouseId,
                            _fulfillment_inventory_compartment = 111,
                            _fulfillment_inventory_postingType = new[] { new { id = 571 } },
                            _fulfillment_inventory_postingText = "Bestandseinbuchung",
                            _fulfillment_inventory_origin = "Middleware import"
                        }
                    };

                    await _actindoClient.PostAsync(endpoints.CreateInventory, payload, cancellationToken);
                    results.Add(new InventoryUpdateResultItem { Sku = item.sku, WarehouseId = item.stock.WarehouseId ?? string.Empty, Success = true });
                }
                catch (Exception ex)
                {
                    results.Add(new InventoryUpdateResultItem { Sku = item.sku, WarehouseId = item.stock.WarehouseId ?? string.Empty, Success = false, Error = ex.Message });
                }
                finally
                {
                    sem.Release();
                    throttler.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            // Speichere Bestandsdaten in DB
            foreach (var item in workItems)
            {
                await _dashboardMetrics.UpdateProductStockAsync(
                    item.sku,
                    (int)(item.stock.Stock ?? 0),
                    item.warehouseId,
                    cancellationToken);
            }

            success = true;
            responsePayload = DashboardPayloadSerializer.Serialize(results);
            return Ok(new { results });
        }
        catch (Exception ex)
        {
            errorPayload = DashboardPayloadSerializer.SerializeError(ex);
            throw;
        }
        finally
        {
            await _dashboardMetrics.CompleteJobAsync(
                jobHandle,
                success,
                stopwatch.Elapsed,
                responsePayload,
                errorPayload,
                cancellationToken);
        }
    }

    [HttpPost("price")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePrices(
        [FromBody] JsonElement body,
        CancellationToken _)
    {
        if (body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return BadRequest("Payload ist erforderlich.");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;

        var jobHandle = await _dashboardMetrics.BeginJobAsync(
            DashboardMetricType.Product,
            DashboardJobEndpoints.ProductPrice,
            body.GetRawText(),
            cancellationToken);

        using var jobScope = DashboardJobContext.Begin(jobHandle.Id);
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        string? responsePayload = null;
        string? errorPayload = null;

        try
        {
            var endpoints = await _endpointProvider.GetAsync(cancellationToken);
            var results = new List<JsonElement>();

            var priceUpdates = new List<(int? actindoId, string? sku, decimal? price, decimal? priceEmployee, decimal? priceMember)>();

            if (body.TryGetProperty("variant_prices", out var variantPrices) &&
                variantPrices.ValueKind == JsonValueKind.Array &&
                variantPrices.GetArrayLength() > 0)
            {
                foreach (var variant in variantPrices.EnumerateArray())
                {
                    var forwarded = new { product = variant, thaw = true };
                    var resp = await _actindoClient.PostAsync(
                        endpoints.SaveProduct,
                        forwarded,
                        cancellationToken);
                    results.Add(resp);

                    // Extrahiere Preisdaten für DB-Update (aus Request und Response)
                    var priceData = ExtractPriceData(variant);
                    var skuFromResponse = ExtractSkuFromResponse(resp);
                    if (!string.IsNullOrWhiteSpace(skuFromResponse))
                        priceData = (priceData.actindoId, skuFromResponse, priceData.price, priceData.priceEmployee, priceData.priceMember);
                    if (priceData.actindoId.HasValue || !string.IsNullOrWhiteSpace(priceData.sku))
                        priceUpdates.Add(priceData);
                }
            }
            else
            {
                var forwarded = new { product = body, thaw = true };
                var resp = await _actindoClient.PostAsync(
                    endpoints.SaveProduct,
                    forwarded,
                    cancellationToken);
                results.Add(resp);

                // Extrahiere Preisdaten für DB-Update (aus Request und Response)
                var priceData = ExtractPriceData(body);
                var skuFromResponse = ExtractSkuFromResponse(resp);
                if (!string.IsNullOrWhiteSpace(skuFromResponse))
                    priceData = (priceData.actindoId, skuFromResponse, priceData.price, priceData.priceEmployee, priceData.priceMember);
                if (priceData.actindoId.HasValue || !string.IsNullOrWhiteSpace(priceData.sku))
                    priceUpdates.Add(priceData);
            }

            // Speichere Preisdaten in DB (versuche erst per ActindoId, dann per SKU)
            foreach (var update in priceUpdates)
            {
                if (update.actindoId.HasValue)
                {
                    await _dashboardMetrics.UpdateProductPriceByActindoIdAsync(
                        update.actindoId.Value,
                        update.price,
                        update.priceEmployee,
                        update.priceMember,
                        cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(update.sku))
                {
                    await _dashboardMetrics.UpdateProductPriceAsync(
                        update.sku,
                        update.price,
                        update.priceEmployee,
                        update.priceMember,
                        cancellationToken);
                }
            }

            success = true;
            responsePayload = DashboardPayloadSerializer.Serialize(results);
            return Ok(new { results });
        }
        catch (Exception ex)
        {
            errorPayload = DashboardPayloadSerializer.SerializeError(ex);
            throw;
        }
        finally
        {
            await _dashboardMetrics.CompleteJobAsync(
                jobHandle,
                success,
                stopwatch.Elapsed,
                responsePayload,
                errorPayload,
                cancellationToken);
        }
    }

    /// <summary>
    /// Erstellt/Speichert ein komplettes Produkt mit Varianten, Preisen und Beständen in einem Request.
    /// </summary>
    [HttpPost("full")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FullProductSync(
        [FromBody] FullProductRequest request,
        CancellationToken _)
    {
        _logger.LogInformation("FullProductSync: ProductValueKind={Kind} Await={Await} BufferId={BufferId}",
            request.Product.ValueKind, request.Await, request.BufferId);

        if (request.Product.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            _logger.LogWarning("FullProductSync 400: Product payload is missing (ValueKind={Kind})", request.Product.ValueKind);
            return BadRequest("Product payload is missing");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var cancellationToken = cts.Token;

        // Capture raw text before any async returns (JsonElement might be invalid after response)
        var rawProduct = request.Product.GetRawText();

        // Quick parse to extract masterSku for lock key and job info
        var quickNode = JsonNode.Parse(rawProduct) as JsonObject;
        if (quickNode is null)
        {
            _logger.LogWarning("FullProductSync 400: Product is not a JSON object");
            return BadRequest("Product must be a JSON object");
        }
        var masterSku = quickNode["sku"]?.ToString() ?? string.Empty;

        if (!request.Await)
        {
            var navError = await ValidateNavSettingsForAsync(cancellationToken);
            if (navError != null)
            {
                _logger.LogWarning("FullProductSync 400: NAV settings not configured for async mode");
                return navError;
            }

            var capturedInventories = request.Inventories;
            var capturedBufferId = request.BufferId;
            var requestPayload = DashboardPayloadSerializer.Serialize(request);

            _jobQueue.Enqueue(masterSku, "full", request.BufferId, async ct =>
            {
                var productLock = ProductLocks.GetOrAdd(masterSku, _ => new SemaphoreSlim(1, 1));
                await productLock.WaitAsync(ct);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var actindoClient = scope.ServiceProvider.GetRequiredService<ActindoClient>();

                    var jobHandle = await _dashboardMetrics.BeginJobAsync(
                        DashboardMetricType.Product, DashboardJobEndpoints.ProductFull, requestPayload, ct);
                    using var jobScope = DashboardJobContext.Begin(jobHandle.Id);
                    var sw = Stopwatch.StartNew();
                    bool success = false; string? responsePayload = null; string? errorPayload = null;

                    try
                    {
                        var results = await RunFullSyncCoreAsync(rawProduct, capturedInventories, actindoClient, ct);

                        var masterNode = JsonNode.Parse(rawProduct) as JsonObject;
                        var masterName = GetNameFromJsonNode(masterNode);
                        var variantNodes = (masterNode?["variants"] as JsonArray)?.OfType<JsonObject>()
                            .ToDictionary(v => v["sku"]?.ToString() ?? string.Empty, v => v) ?? [];

                        var hasVariants = results.Variants.Count > 0;
                        await _dashboardMetrics.SaveProductAsync(
                            jobHandle.Id, masterSku, masterName,
                            results.MasterProductId, hasVariants ? "master" : "single", null, null, ct);

                        foreach (var variant in results.Variants.Where(v => v.Success))
                        {
                            variantNodes.TryGetValue(variant.Sku, out var vNode);
                            await _dashboardMetrics.SaveProductAsync(
                                jobHandle.Id, variant.Sku, GetNameFromJsonNode(vNode),
                                variant.ProductId, "child", masterSku, vNode?["_pim_varcode"]?.ToString(), ct);
                        }

                        success = true;
                        responsePayload = DashboardPayloadSerializer.Serialize(results);
                        await _navCallback.SendCallbackAsync(masterSku, capturedBufferId, results, created: results.MasterOperation == "created", ct);
                    }
                    catch (Exception ex)
                    {
                        errorPayload = DashboardPayloadSerializer.SerializeError(ex);
                        var navAck = await _navCallback.SendCallbackAsync(masterSku, capturedBufferId,
                            new { success = false, error = ex.Message }, created: false, ct);
                        if (!navAck) throw;
                    }
                    finally
                    {
                        await _dashboardMetrics.CompleteJobAsync(
                            jobHandle, success, sw.Elapsed, responsePayload, errorPayload, ct);
                    }
                }
                finally
                {
                    productLock.Release();
                }
            });

            return Accepted(new { message = "Sync wird ausgeführt", bufferId = request.BufferId });
        }

        // Sync path
        var jobHandle2 = await _dashboardMetrics.BeginJobAsync(
            DashboardMetricType.Product,
            DashboardJobEndpoints.ProductFull,
            DashboardPayloadSerializer.Serialize(request),
            cancellationToken);
        using var jobScope2 = DashboardJobContext.Begin(jobHandle2.Id);
        var stopwatch = Stopwatch.StartNew();
        var success2 = false;
        string? responsePayload2 = null;
        string? errorPayload2 = null;
        SemaphoreSlim? fullSyncLock = null;

        try
        {
            fullSyncLock = ProductLocks.GetOrAdd(masterSku, _ => new SemaphoreSlim(1, 1));
            await fullSyncLock.WaitAsync(cancellationToken);

            var results = await RunFullSyncCoreAsync(rawProduct, request.Inventories, _actindoClient, cancellationToken);

            var masterNode2 = JsonNode.Parse(rawProduct) as JsonObject;
            var masterName2 = GetNameFromJsonNode(masterNode2);
            var variantNodes2 = (masterNode2?["variants"] as JsonArray)?.OfType<JsonObject>()
                .ToDictionary(v => v["sku"]?.ToString() ?? string.Empty, v => v) ?? [];

            var hasVariants2 = results.Variants.Count > 0;
            await _dashboardMetrics.SaveProductAsync(
                jobHandle2.Id, masterSku, masterName2,
                results.MasterProductId, hasVariants2 ? "master" : "single", null, null, cancellationToken);

            foreach (var variant in results.Variants.Where(v => v.Success))
            {
                variantNodes2.TryGetValue(variant.Sku, out var vNode);
                await _dashboardMetrics.SaveProductAsync(
                    jobHandle2.Id, variant.Sku, GetNameFromJsonNode(vNode),
                    variant.ProductId, "child", masterSku, vNode?["_pim_varcode"]?.ToString(), cancellationToken);
            }

            success2 = true;
            responsePayload2 = DashboardPayloadSerializer.Serialize(results);
            return Ok(results);
        }
        catch (Exception ex)
        {
            errorPayload2 = DashboardPayloadSerializer.SerializeError(ex);
            throw;
        }
        finally
        {
            fullSyncLock?.Release();
            await _dashboardMetrics.CompleteJobAsync(
                jobHandle2, success2, stopwatch.Elapsed, responsePayload2, errorPayload2, cancellationToken);
        }
    }

    private async Task<FullProductSyncResult> RunFullSyncCoreAsync(
        string rawProduct,
        Dictionary<string, InventoryEntry>? inventories,
        ActindoClient actindoClient,
        CancellationToken cancellationToken)
    {
        var endpoints = await _endpointProvider.GetAsync(cancellationToken);
        var results = new FullProductSyncResult();

        var productNode = JsonNode.Parse(rawProduct);
        if (productNode is not JsonObject productObj)
            throw new InvalidOperationException("Product must be a JSON object");

        var variantsNode = productObj["variants"];
        productObj.Remove("variants");

        var hasId = productObj.ContainsKey("id") &&
                    productObj["id"] is not null &&
                    !string.IsNullOrWhiteSpace(productObj["id"]?.ToString());

        var masterEndpoint = hasId ? endpoints.SaveProduct : endpoints.CreateProduct;
        var masterSku = productObj["sku"]?.ToString() ?? string.Empty;

        // Step 1: Create/Save master product
        var masterPayload = new { product = productObj };
        var masterResponse = await actindoClient.PostAsync(masterEndpoint, masterPayload, cancellationToken);

        int masterProductId;
        if (masterResponse.TryGetProperty("product", out var productProp) &&
            productProp.TryGetProperty("id", out var idProp))
        {
            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt32(out var id))
                masterProductId = id;
            else if (idProp.ValueKind == JsonValueKind.String && int.TryParse(idProp.GetString(), out var idStr))
                masterProductId = idStr;
            else
                throw new InvalidOperationException("Could not read master product ID from response");
        }
        else
        {
            throw new InvalidOperationException("Actindo did not return master product ID");
        }

        results.MasterProductId = masterProductId;
        results.MasterSku = masterSku;
        results.MasterOperation = hasId ? "saved" : "created";

        // Step 2: Process variants sequentially: create/save → changeVariantMaster
        if (variantsNode is JsonArray variantsArray && variantsArray.Count > 0)
        {
            foreach (var variantObj in variantsArray.OfType<JsonObject>())
            {
                var variantSku = variantObj["sku"]?.ToString() ?? string.Empty;
                var variantHasId = variantObj.ContainsKey("id") &&
                                   variantObj["id"] is not null &&
                                   !string.IsNullOrWhiteSpace(variantObj["id"]?.ToString());

                var variantEndpoint = variantHasId ? endpoints.SaveProduct : endpoints.CreateProduct;

                try
                {
                    var isIndi = variantObj["_pim_varcode"]?.ToString()
                        ?.Contains("INDI", StringComparison.OrdinalIgnoreCase) == true;

                    if (isIndi)
                    {
                        variantObj["variantStatus"] = JsonValue.Create("single");
                        variantObj["_pim_flock_name"] = JsonValue.Create("");
                        variantObj["_pim_flock_number"] = JsonValue.Create("");
                    }

                    var variantPayload = new { product = variantObj };
                    var variantResponse = await actindoClient.PostAsync(variantEndpoint, variantPayload, cancellationToken);

                    int variantProductId;
                    if (variantResponse.TryGetProperty("product", out var vProductProp) &&
                        vProductProp.TryGetProperty("id", out var vIdProp))
                    {
                        if (vIdProp.ValueKind == JsonValueKind.Number && vIdProp.TryGetInt32(out var vId))
                            variantProductId = vId;
                        else if (vIdProp.ValueKind == JsonValueKind.String && int.TryParse(vIdProp.GetString(), out var vIdStr))
                            variantProductId = vIdStr;
                        else
                            throw new InvalidOperationException($"Could not read variant product ID for {variantSku}");
                    }
                    else
                    {
                        throw new InvalidOperationException($"Actindo did not return variant product ID for {variantSku}");
                    }

                    // Always call changeVariantMaster to ensure the variant-master relationship is set correctly
                    var relationPayload = new
                    {
                        variantProduct = new { id = variantProductId },
                        parentProduct = new { id = masterProductId }
                    };
                    await actindoClient.PostAsync(endpoints.CreateRelation, relationPayload, cancellationToken);

                    results.Variants.Add(new VariantSyncResultItem
                    {
                        Sku = variantSku,
                        ProductId = variantProductId,
                        Operation = variantHasId ? "saved" : "created",
                        Success = true
                    });
                }
                catch (Exception ex)
                {
                    results.Variants.Add(new VariantSyncResultItem
                    {
                        Sku = variantSku,
                        Success = false,
                        Error = ex.Message
                    });
                }
            }
        }

        // Step 3: Process inventories sequentially
        if (inventories != null && inventories.Count > 0)
        {
            var mappings = (await _settingsStore.GetActindoSettingsAsync(cancellationToken)).WarehouseMappings;

            foreach (var kvp in inventories)
            {
                var sku = kvp.Key;
                var entry = kvp.Value;
                if (string.IsNullOrWhiteSpace(sku) || entry?.Stocks == null || entry.Stocks.Count == 0)
                    continue;

                foreach (var stockEntry in entry.Stocks)
                {
                    if (stockEntry?.WarehouseId is null || stockEntry.Stock is null)
                        continue;

                    if (!mappings.TryGetValue(stockEntry.WarehouseId, out var warehouseId))
                    {
                        results.InventoryUpdates.Add(new InventoryUpdateResultItem
                        {
                            Sku = sku,
                            WarehouseId = stockEntry.WarehouseId,
                            Success = false,
                            Error = $"Lager '{stockEntry.WarehouseId}' ist nicht gemappt. Bitte in den Einstellungen unter 'Lager-Konfiguration' eintragen."
                        });
                        continue;
                    }

                    var payload = new
                    {
                        inventory = new
                        {
                            sku,
                            synchronousSync = true,
                            compareOldValue = true,
                            _fulfillment_inventory_amount = stockEntry.Stock,
                            _fulfillment_inventory_warehouse = warehouseId,
                            _fulfillment_inventory_compartment = 111,
                            _fulfillment_inventory_postingType = new[] { new { id = 571 } },
                            _fulfillment_inventory_postingText = "Bestandseinbuchung",
                            _fulfillment_inventory_origin = "Middleware import"
                        }
                    };

                    try
                    {
                        await actindoClient.PostAsync(endpoints.CreateInventory, payload, cancellationToken);
                        await _dashboardMetrics.UpdateProductStockAsync(sku, (int)(stockEntry.Stock ?? 0), warehouseId, cancellationToken);
                        results.InventoryUpdates.Add(new InventoryUpdateResultItem { Sku = sku, WarehouseId = stockEntry.WarehouseId, Success = true });
                    }
                    catch (Exception ex)
                    {
                        results.InventoryUpdates.Add(new InventoryUpdateResultItem { Sku = sku, WarehouseId = stockEntry.WarehouseId, Success = false, Error = ex.Message });
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Wandelt eine CreateProductResponse in das einheitliche NAV-Callback-Format um (wie FullProductSyncResult).
    /// </summary>
    private static object ToNavCallbackPayload(string masterSku, CreateProductResponse result, bool created) => new
    {
        masterProductId = result.ProductId,
        masterSku,
        masterOperation = created ? "created" : "saved",
        variants = (result.Variants ?? []).Select(v => new
        {
            sku = v.Sku,
            productId = v.Id,
            operation = created ? "created" : "saved",
            success = true
        }).ToList(),
        variantErrors = result.VariantErrors,
        success = result.Success
    };

}

public sealed class FullProductSyncResult
{
    public int MasterProductId { get; set; }
    public string MasterSku { get; set; } = string.Empty;
    public string MasterOperation { get; set; } = string.Empty;
    public List<VariantSyncResultItem> Variants { get; set; } = new();
    public ConcurrentBag<InventoryUpdateResultItem> InventoryUpdates { get; set; } = new();
    public bool Success => Variants.All(v => v.Success) &&
                           InventoryUpdates.All(i => i.Success);
}

public sealed class VariantSyncResultItem
{
    public string Sku { get; set; } = string.Empty;
    public int? ProductId { get; set; }
    public string? Operation { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public sealed class InventoryUpdateResultItem
{
    public string Sku { get; set; } = string.Empty;
    public string WarehouseId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}
