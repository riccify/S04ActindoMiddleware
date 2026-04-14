using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Infrastructure.Actindo;
using ActindoMiddleware.Infrastructure.Nav;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/sync")]
[Authorize(Policy = AuthPolicies.Write)]
public sealed class SyncController : ControllerBase
{
    private readonly INavClient _navClient;
    private readonly ActindoProductListService _actindoProductService;
    private readonly IDashboardMetricsService _dashboardMetrics;
    private readonly ILogger<SyncController> _logger;

    public SyncController(
        INavClient navClient,
        ActindoProductListService actindoProductService,
        IDashboardMetricsService dashboardMetrics,
        ILogger<SyncController> logger)
    {
        _navClient = navClient;
        _actindoProductService = actindoProductService;
        _dashboardMetrics = dashboardMetrics;
        _logger = logger;
    }

    /// <summary>
    /// Check if NAV API is configured
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        return Ok(new { configured });
    }

    /// <summary>
    /// Get product sync status (3-way comparison: Actindo, NAV, Middleware)
    /// </summary>
    [HttpGet("products")]
    public async Task<ActionResult<ProductSyncStatusDto>> GetProductSyncStatus(CancellationToken cancellationToken)
    {
        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get products from all three sources
        IReadOnlyList<ActindoSyncProduct> actindoProducts;
        IReadOnlyList<NavProductRecord> navProducts;
        IReadOnlyList<ProductListItem> middlewareProducts;

        try
        {
            // Parallel fetching for better performance
            var actindoTask = _actindoProductService.GetAllProductsForSyncAsync(cancellationToken);
            var navTask = _navClient.GetProductsAsync(cancellationToken);
            var middlewareTask = _dashboardMetrics.GetCreatedProductsAsync(10000, true, cancellationToken);

            await Task.WhenAll(actindoTask, navTask, middlewareTask);

            actindoProducts = actindoTask.Result;
            navProducts = navTask.Result;
            middlewareProducts = middlewareTask.Result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch products from one or more sources");
            return StatusCode(502, new { error = "Failed to connect to APIs", details = ex.Message });
        }

        // Create lookups (use GroupBy to handle duplicates - take first occurrence)
        var actindoBySku = actindoProducts
            .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var actindoById = actindoProducts
            .GroupBy(p => p.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var navBySku = navProducts
            .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var middlewareBySku = middlewareProducts
            .Where(p => p.VariantStatus != "child") // Only masters and singles
            .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var middlewareChildrenBySku = middlewareProducts
            .Where(p => p.VariantStatus == "child")
            .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Collect all unique SKUs (excluding children - they'll be nested)
        var allMasterSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in actindoProducts.Where(x => x.VariantStatus != "child"))
            allMasterSkus.Add(p.Sku);
        foreach (var p in navProducts)
            allMasterSkus.Add(p.Sku);
        foreach (var p in middlewareProducts.Where(x => x.VariantStatus != "child"))
            allMasterSkus.Add(p.Sku);

        var items = new List<ProductSyncItemDto>();
        var synced = 0;
        var needsSync = 0;
        var mismatch = 0;
        var orphaned = 0;

        // Collect ActindoOnly products that need name fetching
        var actindoOnlyProducts = new List<(string sku, int actindoId)>();

        foreach (var sku in allMasterSkus.OrderBy(s => s))
        {
            actindoBySku.TryGetValue(sku, out var actindo);
            navBySku.TryGetValue(sku, out var nav);
            middlewareBySku.TryGetValue(sku, out var middleware);

            var inActindo = actindo != null;
            var inNav = nav != null;
            var inMiddleware = middleware != null;

            var actindoId = actindo?.Id.ToString();
            var navActindoId = nav?.ActindoId;
            var middlewareActindoId = middleware?.ProductId?.ToString();

            // Determine variant status
            var variantStatus = actindo?.VariantStatus ?? middleware?.VariantStatus ?? "single";
            if (nav?.Variants.Count > 0 && variantStatus == "single")
                variantStatus = "master";

            // Determine sync status
            var status = DetermineStatus(inActindo, inNav, inMiddleware, actindoId, navActindoId, middlewareActindoId);

            // Process variants for master products
            var variants = new List<ProductVariantSyncItemDto>();
            if (variantStatus == "master")
            {
                variants = BuildVariantsList(sku, actindoProducts, nav, middlewareChildrenBySku, actindoById);
            }

            // Count variants status
            var variantNeedsSync = variants.Count(v => v.Status == SyncStatus.NeedsSync);
            var variantOrphaned = variants.Count(v => v.Status == SyncStatus.Orphan);
            var variantMismatch = variants.Count(v => v.Status == SyncStatus.Mismatch);

            // Aggregate status - if any variant has issues, parent reflects it
            if (variantMismatch > 0 && status == SyncStatus.Synced)
                status = SyncStatus.Mismatch;
            if (variantNeedsSync > 0 && status == SyncStatus.Synced)
                status = SyncStatus.NeedsSync;

            switch (status)
            {
                case SyncStatus.Synced:
                    synced++;
                    break;
                case SyncStatus.NeedsSync:
                    needsSync++;
                    break;
                case SyncStatus.Mismatch:
                    mismatch++;
                    break;
                case SyncStatus.Orphan:
                    orphaned++;
                    break;
            }

            // NAV returns names, Actindo getList doesn't - prefer NAV name
            var name = !string.IsNullOrEmpty(nav?.Name) ? nav.Name
                     : !string.IsNullOrEmpty(middleware?.Name) ? middleware.Name
                     : !string.IsNullOrEmpty(actindo?.Name) ? actindo.Name
                     : string.Empty;

            // Track ActindoOnly products for name fetching
            if (status == SyncStatus.ActindoOnly && string.IsNullOrEmpty(name) && actindo != null)
            {
                actindoOnlyProducts.Add((sku, actindo.Id));
            }

            items.Add(new ProductSyncItemDto
            {
                Sku = sku,
                Name = name,
                VariantStatus = variantStatus,
                ActindoId = actindoId,
                NavActindoId = navActindoId,
                MiddlewareActindoId = middlewareActindoId,
                InActindo = inActindo,
                InNav = inNav,
                InMiddleware = inMiddleware,
                Status = status,
                Variants = variants
            });
        }

        // Fetch names for ActindoOnly products from Actindo API
        if (actindoOnlyProducts.Count > 0)
        {
            await FetchActindoOnlyNamesAsync(items, actindoOnlyProducts, cancellationToken);
        }

        return Ok(new ProductSyncStatusDto
        {
            TotalInActindo = actindoProducts.Count(p => p.VariantStatus != "child"),
            TotalInNav = navProducts.Count,
            TotalInMiddleware = middlewareProducts.Count(p => p.VariantStatus != "child"),
            Synced = synced,
            NeedsSync = needsSync,
            Mismatch = mismatch,
            Orphaned = orphaned,
            Items = items
        });
    }

    /// <summary>
    /// Fetch names for ActindoOnly products from Actindo API
    /// </summary>
    private async Task FetchActindoOnlyNamesAsync(
        List<ProductSyncItemDto> items,
        List<(string sku, int actindoId)> actindoOnlyProducts,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching names for {Count} ActindoOnly products", actindoOnlyProducts.Count);

        // Fetch in parallel (max 5 concurrent)
        var semaphore = new SemaphoreSlim(5);
        var tasks = actindoOnlyProducts.Select(async p =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var details = await _actindoProductService.GetProductDetailsAsync(p.actindoId, cancellationToken);
                return (p.sku, details);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        // Update items with fetched names
        var detailsBySku = results
            .Where(r => r.details != null)
            .ToDictionary(r => r.sku, r => r.details!, StringComparer.OrdinalIgnoreCase);

        // Also collect variant details to fetch
        var variantIdsToFetch = new List<(string masterSku, int variantId)>();
        foreach (var (sku, details) in detailsBySku)
        {
            if (details.ChildrenIds.Count > 0)
            {
                foreach (var childId in details.ChildrenIds)
                {
                    variantIdsToFetch.Add((sku, childId));
                }
            }
        }

        // Fetch variant details
        var variantDetails = new Dictionary<int, ActindoProductDetails>();
        if (variantIdsToFetch.Count > 0)
        {
            var variantTasks = variantIdsToFetch.Select(async v =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var details = await _actindoProductService.GetProductDetailsAsync(v.variantId, cancellationToken);
                    return (v.variantId, details);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var variantResults = await Task.WhenAll(variantTasks);
            foreach (var (id, det) in variantResults)
            {
                if (det != null)
                    variantDetails[id] = det;
            }
        }

        // Now update the items list
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (detailsBySku.TryGetValue(item.Sku, out var details))
            {
                // Update master name
                var updatedVariants = item.Variants.ToList();

                // Update variant names using _pim_varcode
                for (var j = 0; j < updatedVariants.Count; j++)
                {
                    var variant = updatedVariants[j];
                    if (string.IsNullOrEmpty(variant.Name) && !string.IsNullOrEmpty(variant.ActindoId))
                    {
                        if (int.TryParse(variant.ActindoId, out var variantId) &&
                            variantDetails.TryGetValue(variantId, out var varDet))
                        {
                            // Use varcode as name for variants
                            var varName = !string.IsNullOrEmpty(varDet.VariantCode)
                                ? varDet.VariantCode
                                : varDet.Name;

                            updatedVariants[j] = new ProductVariantSyncItemDto
                            {
                                Sku = variant.Sku,
                                VariantCode = variant.VariantCode,
                                Name = varName,
                                ActindoId = variant.ActindoId,
                                NavActindoId = variant.NavActindoId,
                                MiddlewareActindoId = variant.MiddlewareActindoId,
                                InActindo = variant.InActindo,
                                InNav = variant.InNav,
                                InMiddleware = variant.InMiddleware,
                                Status = variant.Status
                            };
                        }
                    }
                }

                items[i] = new ProductSyncItemDto
                {
                    Sku = item.Sku,
                    Name = details.Name,
                    VariantStatus = item.VariantStatus,
                    ActindoId = item.ActindoId,
                    NavActindoId = item.NavActindoId,
                    MiddlewareActindoId = item.MiddlewareActindoId,
                    InActindo = item.InActindo,
                    InNav = item.InNav,
                    InMiddleware = item.InMiddleware,
                    Status = item.Status,
                    Variants = updatedVariants
                };
            }
        }
    }

    private List<ProductVariantSyncItemDto> BuildVariantsList(
        string masterSku,
        IReadOnlyList<ActindoSyncProduct> actindoProducts,
        NavProductRecord? nav,
        Dictionary<string, ProductListItem> middlewareChildren,
        Dictionary<string, ActindoSyncProduct> actindoById)
    {
        var variants = new List<ProductVariantSyncItemDto>();

        // Collect all variant SKUs
        var allVariantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // From Actindo (children with matching prefix)
        foreach (var child in actindoProducts.Where(p =>
            p.VariantStatus == "child" &&
            p.Sku.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase)))
        {
            allVariantSkus.Add(child.Sku);
        }

        // From NAV variants
        if (nav?.Variants != null)
        {
            foreach (var v in nav.Variants)
                allVariantSkus.Add(v.NavId);
        }

        // From Middleware children
        foreach (var child in middlewareChildren.Where(kv =>
            kv.Value.ParentSku == masterSku ||
            kv.Key.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase)))
        {
            allVariantSkus.Add(child.Key);
        }

        foreach (var varSku in allVariantSkus.OrderBy(s => s))
        {
            // Find in Actindo
            var actindoChild = actindoProducts.FirstOrDefault(p =>
                p.VariantStatus == "child" &&
                string.Equals(p.Sku, varSku, StringComparison.OrdinalIgnoreCase));

            // Find in NAV
            var navVariant = nav?.Variants.FirstOrDefault(v =>
                string.Equals(v.NavId, varSku, StringComparison.OrdinalIgnoreCase));

            // Find in Middleware
            middlewareChildren.TryGetValue(varSku, out var mwChild);

            var inActindo = actindoChild != null;
            var inNav = navVariant != null;
            var inMiddleware = mwChild != null;

            var actindoId = actindoChild?.Id.ToString();
            var navActindoId = navVariant?.ActindoId;
            var mwActindoId = mwChild?.ProductId?.ToString();

            var status = DetermineStatus(inActindo, inNav, inMiddleware, actindoId, navActindoId, mwActindoId);

            // Extract variant code (part after the dash)
            var variantCode = varSku.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase)
                ? varSku[(masterSku.Length + 1)..]
                : varSku;

            // NAV returns names, Actindo getList doesn't - prefer NAV name
            var name = !string.IsNullOrEmpty(navVariant?.Name) ? navVariant.Name
                     : !string.IsNullOrEmpty(mwChild?.Name) ? mwChild.Name
                     : !string.IsNullOrEmpty(actindoChild?.Name) ? actindoChild.Name
                     : string.Empty;

            variants.Add(new ProductVariantSyncItemDto
            {
                Sku = varSku,
                VariantCode = variantCode,
                Name = name,
                ActindoId = actindoId,
                NavActindoId = navActindoId,
                MiddlewareActindoId = mwActindoId,
                InActindo = inActindo,
                InNav = inNav,
                InMiddleware = inMiddleware,
                Status = status
            });
        }

        return variants;
    }

    private static SyncStatus DetermineStatus(
        bool inActindo,
        bool inNav,
        bool inMiddleware,
        string? actindoId,
        string? navActindoId,
        string? middlewareActindoId)
    {
        // Orphan: Has ID in NAV or Middleware but product doesn't exist in Actindo
        if (!inActindo && (inNav || inMiddleware) && (!string.IsNullOrEmpty(navActindoId) || !string.IsNullOrEmpty(middlewareActindoId)))
        {
            return SyncStatus.Orphan;
        }

        // Mismatch: NAV has an Actindo ID but it doesn't match the actual Actindo ID
        // Actindo is the source of truth for Actindo IDs
        if (inActindo && inNav && !string.IsNullOrEmpty(actindoId) && !string.IsNullOrEmpty(navActindoId))
        {
            if (!string.Equals(actindoId, navActindoId, StringComparison.OrdinalIgnoreCase))
            {
                return SyncStatus.Mismatch;
            }
        }

        // Actindo only: Exists in Actindo but not in NAV or Middleware
        if (inActindo && !inNav && !inMiddleware)
        {
            return SyncStatus.ActindoOnly;
        }

        // NAV only: Exists in NAV but not in Actindo (and no Actindo ID)
        if (inNav && !inActindo && string.IsNullOrEmpty(navActindoId))
        {
            return SyncStatus.NavOnly;
        }

        // Needs sync: Middleware has Actindo ID but NAV doesn't
        if (!string.IsNullOrEmpty(middlewareActindoId) && string.IsNullOrEmpty(navActindoId))
        {
            return SyncStatus.NeedsSync;
        }

        // Also needs sync: Actindo ID exists but NAV doesn't have it
        if (!string.IsNullOrEmpty(actindoId) && inNav && string.IsNullOrEmpty(navActindoId))
        {
            return SyncStatus.NeedsSync;
        }

        // Synced: All systems have matching IDs
        if (inActindo && inNav && !string.IsNullOrEmpty(navActindoId))
        {
            return SyncStatus.Synced;
        }

        // Default to synced if nothing else matches
        return SyncStatus.Synced;
    }

    /// <summary>
    /// Get customer sync status
    /// </summary>
    [HttpGet("customers")]
    public async Task<ActionResult<CustomerSyncStatusDto>> GetCustomerSyncStatus(CancellationToken cancellationToken)
    {
        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get customers from Middleware
        var middlewareCustomers = await _dashboardMetrics.GetCreatedCustomersAsync(10000, cancellationToken);

        // Get customers from NAV
        IReadOnlyList<NavCustomerRecord> navCustomers;
        try
        {
            navCustomers = await _navClient.GetCustomersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get customers from NAV");
            return StatusCode(502, new { error = "Failed to connect to NAV API", details = ex.Message });
        }

        // Create lookup by NAV ID (DebtorNumber)
        var navByDebtorNumber = navCustomers.ToDictionary(
            c => c.NavId.ToString(),
            StringComparer.OrdinalIgnoreCase);

        var items = new List<CustomerSyncItemDto>();
        var needsSyncCount = 0;
        var synced = 0;

        foreach (var customer in middlewareCustomers)
        {
            navByDebtorNumber.TryGetValue(customer.DebtorNumber, out var navCustomer);

            var hasMiddlewareId = customer.CustomerId.HasValue;
            var hasNavId = navCustomer?.ActindoId.HasValue == true;
            var requiresSync = hasMiddlewareId && !hasNavId;

            if (requiresSync)
                needsSyncCount++;
            else if (hasMiddlewareId && hasNavId)
                synced++;

            items.Add(new CustomerSyncItemDto
            {
                DebtorNumber = customer.DebtorNumber,
                Name = customer.Name,
                MiddlewareActindoId = customer.CustomerId,
                NavNavId = navCustomer?.NavId,
                NavActindoId = navCustomer?.ActindoId,
                NeedsSync = requiresSync
            });
        }

        return Ok(new CustomerSyncStatusDto
        {
            TotalInMiddleware = middlewareCustomers.Count,
            TotalInNav = navCustomers.Count,
            Synced = synced,
            NeedsSync = needsSyncCount,
            Items = items
        });
    }

    /// <summary>
    /// Sync selected products to NAV
    /// </summary>
    [HttpPost("products")]
    public async Task<ActionResult> SyncProducts([FromBody] SyncProductsRequest request, CancellationToken cancellationToken)
    {
        if (request.Skus.Count == 0)
        {
            return BadRequest(new { error = "No products specified" });
        }

        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get products from all sources
        var actindoProducts = await _actindoProductService.GetAllProductsForSyncAsync(cancellationToken);
        var navProducts = await _navClient.GetProductsAsync(cancellationToken);
        var middlewareProducts = await _dashboardMetrics.GetCreatedProductsAsync(10000, true, cancellationToken);

        var actindoBySku = actindoProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        var navBySku = navProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        var middlewareBySku = middlewareProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var toSync = new List<NavProductSyncRequest>();

        foreach (var sku in request.Skus)
        {
            // Get Actindo ID from either Actindo directly or Middleware
            string? actindoId = null;

            if (actindoBySku.TryGetValue(sku, out var actindoProduct))
            {
                actindoId = actindoProduct.Id.ToString();
            }
            else if (middlewareBySku.TryGetValue(sku, out var mwProduct) && mwProduct.ProductId.HasValue)
            {
                actindoId = mwProduct.ProductId.Value.ToString();
            }

            if (string.IsNullOrEmpty(actindoId))
            {
                _logger.LogWarning("Product {Sku} has no Actindo ID", sku);
                continue;
            }

            if (!navBySku.TryGetValue(sku, out var navProduct))
            {
                _logger.LogWarning("Product {Sku} not found in NAV", sku);
                continue;
            }

            var variantSyncs = BuildVariantSyncRequests(
                sku,
                navProduct,
                actindoProducts,
                actindoBySku,
                middlewareBySku,
                includeAlreadySyncedVariants: false);

            // Only sync if NAV doesn't already have the Actindo ID
            if (string.IsNullOrEmpty(navProduct.ActindoId) || variantSyncs.Count > 0)
            {
                toSync.Add(new NavProductSyncRequest
                {
                    NavId = sku,
                    ActindoId = actindoId,
                    Variants = variantSyncs.Count > 0 ? variantSyncs : null
                });
            }
        }

        if (toSync.Count == 0)
        {
            return Ok(new { synced = 0, message = "No products need syncing" });
        }

        try
        {
            await _navClient.SetProductActindoIdsAsync(toSync, cancellationToken);
            _logger.LogInformation("Synced {Count} products to NAV", toSync.Count);
            return Ok(new { synced = toSync.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync products to NAV");
            return StatusCode(502, new { error = "Failed to sync to NAV API", details = ex.Message });
        }
    }

    /// <summary>
    /// Sync a customer to NAV
    /// </summary>
    [HttpPost("customers")]
    public async Task<ActionResult> SyncCustomer([FromBody] SyncCustomerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DebtorNumber))
        {
            return BadRequest(new { error = "No customer specified" });
        }

        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get customers from Middleware
        var middlewareCustomers = await _dashboardMetrics.GetCreatedCustomersAsync(10000, cancellationToken);
        var customer = middlewareCustomers.FirstOrDefault(c =>
            string.Equals(c.DebtorNumber, request.DebtorNumber, StringComparison.OrdinalIgnoreCase));

        if (customer == null || !customer.CustomerId.HasValue)
        {
            return BadRequest(new { error = "Customer not found in Middleware or has no Actindo ID" });
        }

        // Get customers from NAV to get the NAV ID
        var navCustomers = await _navClient.GetCustomersAsync(cancellationToken);
        var navCustomer = navCustomers.FirstOrDefault(c =>
            c.NavId.ToString() == request.DebtorNumber);

        if (navCustomer == null)
        {
            return BadRequest(new { error = "Customer not found in NAV" });
        }

        try
        {
            await _navClient.SetCustomerActindoIdAsync(navCustomer.NavId, customer.CustomerId.Value, cancellationToken);
            _logger.LogInformation("Synced customer {DebtorNumber} to NAV", request.DebtorNumber);
            return Ok(new { synced = 1 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync customer to NAV");
            return StatusCode(502, new { error = "Failed to sync to NAV API", details = ex.Message });
        }
    }

    /// <summary>
    /// Sync all products that need syncing to NAV
    /// </summary>
    [HttpPost("products/all")]
    public async Task<ActionResult> SyncAllProducts(CancellationToken cancellationToken)
    {
        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get products from all sources
        var actindoProducts = await _actindoProductService.GetAllProductsForSyncAsync(cancellationToken);
        var navProducts = await _navClient.GetProductsAsync(cancellationToken);
        var middlewareProducts = await _dashboardMetrics.GetCreatedProductsAsync(10000, true, cancellationToken);

        var actindoBySku = actindoProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        var middlewareBySku = middlewareProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var toSync = new List<NavProductSyncRequest>();

        foreach (var navProduct in navProducts)
        {
            // Get Actindo ID from either Actindo directly or Middleware
            string? actindoId = null;

            if (actindoBySku.TryGetValue(navProduct.Sku, out var actindoProduct))
            {
                actindoId = actindoProduct.Id.ToString();
            }
            else if (middlewareBySku.TryGetValue(navProduct.Sku, out var mwProduct) && mwProduct.ProductId.HasValue)
            {
                actindoId = mwProduct.ProductId.Value.ToString();
            }

            if (string.IsNullOrEmpty(actindoId))
                continue;

            var variantSyncs = BuildVariantSyncRequests(
                navProduct.Sku,
                navProduct,
                actindoProducts,
                actindoBySku,
                middlewareBySku,
                includeAlreadySyncedVariants: false);

            // Only sync if NAV doesn't already have the Actindo ID or has variants to sync
            var needsMasterSync = string.IsNullOrEmpty(navProduct.ActindoId);
            var needsVariantSync = variantSyncs.Count > 0;

            if (needsMasterSync || needsVariantSync)
            {
                toSync.Add(new NavProductSyncRequest
                {
                    NavId = navProduct.Sku,
                    ActindoId = actindoId,
                    Variants = variantSyncs.Count > 0 ? variantSyncs : null
                });
            }
        }

        if (toSync.Count == 0)
        {
            return Ok(new { synced = 0, message = "No products need syncing" });
        }

        try
        {
            await _navClient.SetProductActindoIdsAsync(toSync, cancellationToken);
            _logger.LogInformation("Synced {Count} products to NAV", toSync.Count);
            return Ok(new { synced = toSync.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync products to NAV");
            return StatusCode(502, new { error = "Failed to sync to NAV API", details = ex.Message });
        }
    }

    /// <summary>
    /// Sync all customers that need syncing to NAV
    /// </summary>
    [HttpPost("customers/all")]
    public async Task<ActionResult> SyncAllCustomers(CancellationToken cancellationToken)
    {
        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get customers from Middleware
        var middlewareCustomers = await _dashboardMetrics.GetCreatedCustomersAsync(10000, cancellationToken);

        // Get customers from NAV
        var navCustomers = await _navClient.GetCustomersAsync(cancellationToken);
        var navByDebtorNumber = navCustomers.ToDictionary(c => c.NavId.ToString(), StringComparer.OrdinalIgnoreCase);

        var synced = 0;
        var errors = new List<string>();

        foreach (var customer in middlewareCustomers)
        {
            if (!customer.CustomerId.HasValue)
                continue;

            if (!navByDebtorNumber.TryGetValue(customer.DebtorNumber, out var navCustomer))
                continue;

            // Only sync if NAV doesn't have the Actindo ID yet
            if (navCustomer.ActindoId.HasValue)
                continue;

            try
            {
                await _navClient.SetCustomerActindoIdAsync(navCustomer.NavId, customer.CustomerId.Value, cancellationToken);
                synced++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync customer {DebtorNumber}", customer.DebtorNumber);
                errors.Add($"Customer {customer.DebtorNumber}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return Ok(new { synced, errors });
        }

        return Ok(new { synced });
    }

    /// <summary>
    /// Clear Actindo IDs for selected products in NAV
    /// </summary>
    [HttpPost("products/clear")]
    public async Task<ActionResult> ClearProductIds([FromBody] SyncProductsRequest request, CancellationToken cancellationToken)
    {
        if (request.Skus.Count == 0)
        {
            return BadRequest(new { error = "No products specified" });
        }

        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get products from NAV to find variants
        var navProducts = await _navClient.GetProductsAsync(cancellationToken);
        var navBySku = navProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var toClear = new List<NavProductClearRequest>();

        foreach (var sku in request.Skus)
        {
            if (!navBySku.TryGetValue(sku, out var navProduct))
            {
                _logger.LogWarning("Product {Sku} not found in NAV", sku);
                continue;
            }

            // Collect variant codes
            List<string>? variantNavIds = null;
            if (navProduct.Variants.Count > 0)
            {
                variantNavIds = navProduct.Variants.Select(v => v.NavId).ToList();
            }

            toClear.Add(new NavProductClearRequest
            {
                NavId = sku,
                VariantNavIds = variantNavIds
            });
        }

        if (toClear.Count == 0)
        {
            return Ok(new { cleared = 0, message = "No products found to clear" });
        }

        try
        {
            await _navClient.ClearProductActindoIdsAsync(toClear, cancellationToken);
            _logger.LogInformation("Cleared Actindo IDs for {Count} products in NAV", toClear.Count);
            return Ok(new { cleared = toClear.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear product IDs in NAV");
            return StatusCode(502, new { error = "Failed to clear IDs in NAV API", details = ex.Message });
        }
    }

    /// <summary>
    /// Force sync Actindo IDs to NAV (overwrite existing IDs)
    /// </summary>
    [HttpPost("products/force")]
    public async Task<ActionResult> ForceSyncProducts([FromBody] SyncProductsRequest request, CancellationToken cancellationToken)
    {
        if (request.Skus.Count == 0)
        {
            return BadRequest(new { error = "No products specified" });
        }

        var configured = await _navClient.IsConfiguredAsync(cancellationToken);
        if (!configured)
        {
            return BadRequest(new { error = "NAV API is not configured" });
        }

        // Get products from all sources
        var actindoProducts = await _actindoProductService.GetAllProductsForSyncAsync(cancellationToken);
        var navProducts = await _navClient.GetProductsAsync(cancellationToken);
        var middlewareProducts = await _dashboardMetrics.GetCreatedProductsAsync(10000, true, cancellationToken);

        var actindoBySku = actindoProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        var navBySku = navProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
        var middlewareBySku = middlewareProducts.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var toSync = new List<NavProductSyncRequest>();

        foreach (var sku in request.Skus)
        {
            // Get Actindo ID from either Actindo directly or Middleware
            string? actindoId = null;

            if (actindoBySku.TryGetValue(sku, out var actindoProduct))
            {
                actindoId = actindoProduct.Id.ToString();
            }
            else if (middlewareBySku.TryGetValue(sku, out var mwProduct) && mwProduct.ProductId.HasValue)
            {
                actindoId = mwProduct.ProductId.Value.ToString();
            }

            if (string.IsNullOrEmpty(actindoId))
            {
                _logger.LogWarning("Product {Sku} has no Actindo ID to sync", sku);
                continue;
            }

            if (!navBySku.TryGetValue(sku, out var navProduct))
            {
                _logger.LogWarning("Product {Sku} not found in NAV", sku);
                continue;
            }

            var variantSyncs = BuildVariantSyncRequests(
                sku,
                navProduct,
                actindoProducts,
                actindoBySku,
                middlewareBySku,
                includeAlreadySyncedVariants: true);

            toSync.Add(new NavProductSyncRequest
            {
                NavId = navProduct.Sku,
                ActindoId = actindoId,
                Variants = variantSyncs.Count > 0 ? variantSyncs : null
            });
        }

        if (toSync.Count == 0)
        {
            return Ok(new { synced = 0, message = "No products to sync" });
        }

        try
        {
            await _navClient.SetProductActindoIdsAsync(toSync, cancellationToken);
            _logger.LogInformation("Force synced {Count} products to NAV", toSync.Count);
            return Ok(new { synced = toSync.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to force sync products to NAV");
            return StatusCode(502, new { error = "Failed to sync to NAV API", details = ex.Message });
        }
    }

    private static List<NavVariantSyncRequest> BuildVariantSyncRequests(
        string masterSku,
        NavProductRecord navProduct,
        IReadOnlyList<ActindoSyncProduct> actindoProducts,
        IReadOnlyDictionary<string, ActindoSyncProduct> actindoBySku,
        IReadOnlyDictionary<string, ProductListItem> middlewareBySku,
        bool includeAlreadySyncedVariants)
    {
        var navVariantsBySku = navProduct.Variants
            .GroupBy(v => v.NavId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var candidateVariantSkus = actindoProducts
            .Where(p => p.VariantStatus == "child" &&
                        p.Sku.StartsWith(masterSku + "-", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Sku)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sku => sku, StringComparer.OrdinalIgnoreCase);

        var result = new List<NavVariantSyncRequest>();

        foreach (var variantSku in candidateVariantSkus)
        {
            navVariantsBySku.TryGetValue(variantSku, out var navVariant);

            if (!includeAlreadySyncedVariants && !string.IsNullOrEmpty(navVariant?.ActindoId))
                continue;

            string? variantActindoId = null;
            if (actindoBySku.TryGetValue(variantSku, out var actindoVariant))
            {
                variantActindoId = actindoVariant.Id.ToString();
            }
            else if (middlewareBySku.TryGetValue(variantSku, out var mwVariant) && mwVariant.ProductId.HasValue)
            {
                variantActindoId = mwVariant.ProductId.Value.ToString();
            }

            if (!string.IsNullOrEmpty(variantActindoId))
            {
                result.Add(new NavVariantSyncRequest
                {
                    NavId = variantSku,
                    ActindoId = variantActindoId
                });
            }
        }

        return result;
    }
}
