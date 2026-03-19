namespace ActindoMiddleware.DTOs.Responses;

/// <summary>
/// Sync status enum
/// </summary>
public enum SyncStatus
{
    /// <summary>All systems are in sync</summary>
    Synced,
    /// <summary>Actindo ID needs to be synced to NAV</summary>
    NeedsSync,
    /// <summary>NAV has wrong Actindo ID (doesn't match actual Actindo ID)</summary>
    Mismatch,
    /// <summary>Product exists in NAV/Middleware but was deleted from Actindo</summary>
    Orphan,
    /// <summary>Product only exists in Actindo, not known to Middleware</summary>
    ActindoOnly,
    /// <summary>Product only exists in NAV</summary>
    NavOnly
}

/// <summary>
/// Status of a single product variant for sync
/// </summary>
public sealed class ProductVariantSyncItemDto
{
    public string Sku { get; init; } = string.Empty;
    public string VariantCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ActindoId { get; init; }
    public string? NavActindoId { get; init; }
    public string? MiddlewareActindoId { get; init; }
    public bool InActindo { get; init; }
    public bool InNav { get; init; }
    public bool InMiddleware { get; init; }
    public SyncStatus Status { get; init; }
}

/// <summary>
/// Status of a single product for sync (with nested variants)
/// </summary>
public sealed class ProductSyncItemDto
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string VariantStatus { get; init; } = "single";

    // IDs from each system
    public string? ActindoId { get; init; }
    public string? NavActindoId { get; init; }
    public string? MiddlewareActindoId { get; init; }

    // Presence in each system
    public bool InActindo { get; init; }
    public bool InNav { get; init; }
    public bool InMiddleware { get; init; }

    // Computed status
    public SyncStatus Status { get; init; }
    public bool NeedsSync => Status == SyncStatus.NeedsSync;
    public bool IsOrphan => Status == SyncStatus.Orphan;
    public bool IsMismatch => Status == SyncStatus.Mismatch;

    // Nested variants for master products
    public IReadOnlyList<ProductVariantSyncItemDto> Variants { get; init; } = [];
}

/// <summary>
/// Status of a single customer for sync
/// </summary>
public sealed class CustomerSyncItemDto
{
    public string DebtorNumber { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? MiddlewareActindoId { get; init; }
    public int? NavNavId { get; init; }
    public int? NavActindoId { get; init; }
    public bool NeedsSync { get; init; }
}

/// <summary>
/// Summary of sync status for products (3-way comparison)
/// </summary>
public sealed class ProductSyncStatusDto
{
    public int TotalInActindo { get; init; }
    public int TotalInNav { get; init; }
    public int TotalInMiddleware { get; init; }
    public int Synced { get; init; }
    public int NeedsSync { get; init; }
    public int Mismatch { get; init; }
    public int Orphaned { get; init; }
    public IReadOnlyList<ProductSyncItemDto> Items { get; init; } = [];
}

/// <summary>
/// Summary of sync status for customers
/// </summary>
public sealed class CustomerSyncStatusDto
{
    public int TotalInMiddleware { get; init; }
    public int TotalInNav { get; init; }
    public int Synced { get; init; }
    public int NeedsSync { get; init; }
    public IReadOnlyList<CustomerSyncItemDto> Items { get; init; } = [];
}

/// <summary>
/// A single variant with a sync problem (missing or ID mismatch)
/// </summary>
public sealed class NavSyncMissingVariant
{
    public string Sku { get; init; } = string.Empty;
    public int ActindoId { get; init; }
    /// <summary>"missing" or "mismatch"</summary>
    public string Status { get; init; } = "missing";
    /// <summary>The wrong ID currently stored in NAV (only set when Status == "mismatch")</summary>
    public string? NavActindoId { get; init; }
}

/// <summary>
/// A product (or master with variants) that has a sync problem with NAV
/// </summary>
public sealed class NavSyncMissingItem
{
    public string Sku { get; init; } = string.Empty;
    public int ActindoId { get; init; }
    public string VariantStatus { get; init; } = "single";
    public int TotalVariants { get; init; }
    /// <summary>"missing" or "mismatch"</summary>
    public string Status { get; init; } = "missing";
    /// <summary>The wrong ID currently stored in NAV (only set when Status == "mismatch")</summary>
    public string? NavActindoId { get; init; }
    public IReadOnlyList<NavSyncMissingVariant> MissingVariants { get; init; } = [];
}

/// <summary>
/// Result of the NAV sync-error check
/// </summary>
public sealed class NavSyncErrorsDto
{
    public int TotalInActindo { get; init; }
    public int MissingFromNav { get; init; }
    public IReadOnlyList<NavSyncMissingItem> Items { get; init; } = [];
}

/// <summary>
/// Request to sync products to NAV
/// </summary>
public sealed class SyncProductsRequest
{
    public IReadOnlyList<string> Skus { get; init; } = [];
}

/// <summary>
/// Request to sync a customer to NAV
/// </summary>
public sealed class SyncCustomerRequest
{
    public string DebtorNumber { get; init; } = string.Empty;
}
