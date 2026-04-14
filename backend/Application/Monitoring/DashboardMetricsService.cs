using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Application.Monitoring;

public interface IDashboardMetricsService
{
    Task<DashboardMetricsSnapshot> GetSnapshotAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductListItem>> GetCreatedProductsAsync(
        int limit,
        bool includeVariants,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductListItem>> GetVariantsForMasterAsync(
        string masterSku,
        CancellationToken cancellationToken = default);

    Task SaveProductAsync(
        Guid jobId,
        string sku,
        string name,
        int? actindoProductId,
        string variantStatus,
        string? parentSku,
        string? variantCode,
        CancellationToken cancellationToken = default);

    Task UpdateProductPriceAsync(
        string sku,
        decimal? price,
        decimal? priceEmployee,
        decimal? priceMember,
        CancellationToken cancellationToken = default);

    Task UpdateProductPriceByActindoIdAsync(
        int actindoProductId,
        decimal? price,
        decimal? priceEmployee,
        decimal? priceMember,
        CancellationToken cancellationToken = default);

    Task UpdateProductStockAsync(
        string sku,
        int stock,
        int warehouseId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductStockItem>> GetProductStocksAsync(
        string sku,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerListItem>> GetCreatedCustomersAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task SaveCustomerAsync(
        Guid jobId,
        int actindoCustomerId,
        string debtorNumber,
        string name,
        CancellationToken cancellationToken = default);

}

public enum DashboardMetricType
{
    Product = 0,
    Customer = 1,
    Transaction = 2,
    Media = 3
}

public sealed record MetricSnapshot
{
    public int Total { get; init; }
    public int Success { get; init; }
    public int Failed { get; init; }
    public double AverageDurationSeconds { get; init; }

    public static MetricSnapshot Empty => new()
    {
        Total = 0,
        Success = 0,
        Failed = 0,
        AverageDurationSeconds = 0
    };
}

public sealed record DashboardMetricsSnapshot
{
    public int ActiveJobs { get; init; }
    public MetricSnapshot ProductStats { get; init; } = MetricSnapshot.Empty;
    public MetricSnapshot CustomerStats { get; init; } = MetricSnapshot.Empty;
    public MetricSnapshot TransactionStats { get; init; } = MetricSnapshot.Empty;
    public MetricSnapshot MediaStats { get; init; } = MetricSnapshot.Empty;
}

public sealed class DashboardMetricsService : IDashboardMetricsService
{
    private const string DefaultConnectionString = "Data Source=App_Data/dashboard.db";
    private readonly string _connectionString;
    private readonly ProductJobQueue _jobQueue;
    private readonly ILogger<DashboardMetricsService> _logger;
    private bool _initialized;
    private readonly object _initializationLock = new();

    public DashboardMetricsService(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ProductJobQueue jobQueue,
        ILogger<DashboardMetricsService> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(jobQueue);
        ArgumentNullException.ThrowIfNull(logger);

        _jobQueue = jobQueue;
        _logger = logger;
        _connectionString = BuildConnectionString(
            configuration.GetConnectionString("Dashboard"),
            hostEnvironment.ContentRootPath);
    }

    public async Task<DashboardMetricsSnapshot> GetSnapshotAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        var cutoff = DateTimeOffset.UtcNow - window;
        var jobs = _jobQueue.GetAll();
        var activeJobs = jobs.Count(job =>
            job.Status is ProductSyncJobStatus.Queued or ProductSyncJobStatus.Running);

        var scopedJobs = jobs
            .Where(job =>
                job.QueuedAt >= cutoff ||
                job.StartedAt >= cutoff ||
                job.CompletedAt >= cutoff)
            .ToList();

        var productTotal = await CountAsync(
            "SELECT COUNT(*) FROM Products;",
            cancellationToken);
        var customerTotal = await CountAsync(
            "SELECT COUNT(*) FROM Customers;",
            cancellationToken);

        return new DashboardMetricsSnapshot
        {
            ActiveJobs = activeJobs,
            ProductStats = BuildMetricSnapshot(
                productTotal,
                scopedJobs.Where(job => IsProductOperation(job.Operation))),
            CustomerStats = BuildMetricSnapshot(
                customerTotal,
                scopedJobs.Where(job => IsCustomerOperation(job.Operation))),
            TransactionStats = BuildMetricSnapshot(
                scopedJobs.Count(job => IsTransactionOperation(job.Operation)),
                scopedJobs.Where(job => IsTransactionOperation(job.Operation))),
            MediaStats = BuildMetricSnapshot(
                scopedJobs.Count(job => IsMediaOperation(job.Operation)),
                scopedJobs.Where(job => IsMediaOperation(job.Operation)))
        };
    }

    public async Task<IReadOnlyList<ProductListItem>> GetCreatedProductsAsync(
        int limit,
        bool includeVariants,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        var products = new List<ProductListItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = includeVariants
            ? """
              SELECT p.Id,
                     p.JobId,
                     p.ActindoProductId,
                     p.Sku,
                     p.Name,
                     p.VariantStatus,
                     p.ParentSku,
                     p.VariantCode,
                     p.CreatedAt,
                     (SELECT COUNT(*) FROM Products c WHERE c.ParentSku = p.Sku) AS VariantCount,
                     p.LastPrice,
                     p.LastPriceEmployee,
                     p.LastPriceMember,
                     (SELECT SUM(Stock) FROM ProductStocks s WHERE s.Sku = p.Sku) AS TotalStock,
                     p.LastWarehouseId,
                     p.LastPriceUpdatedAt,
                     p.LastStockUpdatedAt
              FROM Products p
              ORDER BY p.CreatedAt DESC
              LIMIT @limit;
              """
            : """
              SELECT p.Id,
                     p.JobId,
                     p.ActindoProductId,
                     p.Sku,
                     p.Name,
                     p.VariantStatus,
                     p.ParentSku,
                     p.VariantCode,
                     p.CreatedAt,
                     (SELECT COUNT(*) FROM Products c WHERE c.ParentSku = p.Sku) AS VariantCount,
                     p.LastPrice,
                     p.LastPriceEmployee,
                     p.LastPriceMember,
                     (SELECT SUM(Stock) FROM ProductStocks s WHERE s.Sku = p.Sku) AS TotalStock,
                     p.LastWarehouseId,
                     p.LastPriceUpdatedAt,
                     p.LastStockUpdatedAt
              FROM Products p
              WHERE p.VariantStatus != 'child'
              ORDER BY p.CreatedAt DESC
              LIMIT @limit;
              """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(MapProductListItem(reader));
        }

        return products;
    }

    private static ProductListItem MapProductListItem(SqliteDataReader reader)
    {
        var createdAtStr = reader.IsDBNull(8) ? null : reader.GetString(8);
        DateTimeOffset? createdAt = null;
        if (createdAtStr != null && DateTimeOffset.TryParse(createdAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            createdAt = parsed;
        }

        var variantCount = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);

        var priceUpdatedAtStr = reader.IsDBNull(15) ? null : reader.GetString(15);
        DateTimeOffset? priceUpdatedAt = null;
        if (priceUpdatedAtStr != null && DateTimeOffset.TryParse(priceUpdatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedPrice))
        {
            priceUpdatedAt = parsedPrice;
        }

        var stockUpdatedAtStr = reader.IsDBNull(16) ? null : reader.GetString(16);
        DateTimeOffset? stockUpdatedAt = null;
        if (stockUpdatedAtStr != null && DateTimeOffset.TryParse(stockUpdatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedStock))
        {
            stockUpdatedAt = parsedStock;
        }

        return new ProductListItem
        {
            JobId = Guid.Parse(reader.GetString(1)),
            ProductId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Sku = reader.GetString(3),
            Name = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            VariantStatus = reader.GetString(5),
            ParentSku = reader.IsDBNull(6) ? null : reader.GetString(6),
            VariantCode = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = createdAt,
            VariantCount = variantCount > 0 ? variantCount : null,
            LastPrice = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            LastPriceEmployee = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            LastPriceMember = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            LastStock = reader.IsDBNull(13) ? null : reader.GetInt32(13),
            LastWarehouseId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            LastPriceUpdatedAt = priceUpdatedAt,
            LastStockUpdatedAt = stockUpdatedAt
        };
    }

    public async Task<IReadOnlyList<ProductListItem>> GetVariantsForMasterAsync(
        string masterSku,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (string.IsNullOrWhiteSpace(masterSku))
            return Array.Empty<ProductListItem>();

        var products = new List<ProductListItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.Id,
                   p.JobId,
                   p.ActindoProductId,
                   p.Sku,
                   p.Name,
                   p.VariantStatus,
                   p.ParentSku,
                   p.VariantCode,
                   p.CreatedAt,
                   0 AS VariantCount,
                   p.LastPrice,
                   p.LastPriceEmployee,
                   p.LastPriceMember,
                   (SELECT SUM(Stock) FROM ProductStocks s WHERE s.Sku = p.Sku) AS TotalStock,
                   p.LastWarehouseId,
                   p.LastPriceUpdatedAt,
                   p.LastStockUpdatedAt
            FROM Products p
            WHERE p.ParentSku = @masterSku
            ORDER BY p.Sku;
            """;
        command.Parameters.AddWithValue("@masterSku", masterSku);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(MapProductListItem(reader));
        }

        return products;
    }

    public async Task SaveProductAsync(
        Guid jobId,
        string sku,
        string name,
        int? actindoProductId,
        string variantStatus,
        string? parentSku,
        string? variantCode,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Try to update existing row first
        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText =
            """
            UPDATE Products
            SET JobId = @jobId,
                ActindoProductId = COALESCE(@actindoProductId, ActindoProductId),
                Name = @name,
                VariantStatus = @variantStatus,
                ParentSku = @parentSku,
                VariantCode = @variantCode
            WHERE Sku = @sku;
            """;
        updateCommand.Parameters.AddWithValue("@jobId", jobId.ToString());
        updateCommand.Parameters.AddWithValue("@actindoProductId", actindoProductId.HasValue ? actindoProductId.Value : DBNull.Value);
        updateCommand.Parameters.AddWithValue("@sku", sku);
        updateCommand.Parameters.AddWithValue("@name", name ?? string.Empty);
        updateCommand.Parameters.AddWithValue("@variantStatus", variantStatus);
        updateCommand.Parameters.AddWithValue("@parentSku", (object?)parentSku ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@variantCode", (object?)variantCode ?? DBNull.Value);

        var rowsUpdated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        if (rowsUpdated > 0)
            return;

        // Row doesn't exist yet — insert
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            INSERT INTO Products (Id, JobId, ActindoProductId, Sku, Name, VariantStatus, ParentSku, VariantCode, CreatedAt)
            VALUES (@id, @jobId, @actindoProductId, @sku, @name, @variantStatus, @parentSku, @variantCode, @createdAt);
            """;
        insertCommand.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        insertCommand.Parameters.AddWithValue("@jobId", jobId.ToString());
        insertCommand.Parameters.AddWithValue("@actindoProductId", actindoProductId.HasValue ? actindoProductId.Value : DBNull.Value);
        insertCommand.Parameters.AddWithValue("@sku", sku);
        insertCommand.Parameters.AddWithValue("@name", name ?? string.Empty);
        insertCommand.Parameters.AddWithValue("@variantStatus", variantStatus);
        insertCommand.Parameters.AddWithValue("@parentSku", (object?)parentSku ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("@variantCode", (object?)variantCode ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProductPriceAsync(
        string sku,
        decimal? price,
        decimal? priceEmployee,
        decimal? priceMember,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (string.IsNullOrWhiteSpace(sku))
            return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Products
            SET LastPrice = @price,
                LastPriceEmployee = @priceEmployee,
                LastPriceMember = @priceMember,
                LastPriceUpdatedAt = @updatedAt
            WHERE Sku = @sku;
            """;

        command.Parameters.AddWithValue("@sku", sku);
        command.Parameters.AddWithValue("@price", price.HasValue ? price.Value : DBNull.Value);
        command.Parameters.AddWithValue("@priceEmployee", priceEmployee.HasValue ? priceEmployee.Value : DBNull.Value);
        command.Parameters.AddWithValue("@priceMember", priceMember.HasValue ? priceMember.Value : DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProductPriceByActindoIdAsync(
        int actindoProductId,
        decimal? price,
        decimal? priceEmployee,
        decimal? priceMember,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Products
            SET LastPrice = @price,
                LastPriceEmployee = @priceEmployee,
                LastPriceMember = @priceMember,
                LastPriceUpdatedAt = @updatedAt
            WHERE ActindoProductId = @actindoProductId;
            """;

        command.Parameters.AddWithValue("@actindoProductId", actindoProductId);
        command.Parameters.AddWithValue("@price", price.HasValue ? price.Value : DBNull.Value);
        command.Parameters.AddWithValue("@priceEmployee", priceEmployee.HasValue ? priceEmployee.Value : DBNull.Value);
        command.Parameters.AddWithValue("@priceMember", priceMember.HasValue ? priceMember.Value : DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProductStockAsync(
        string sku,
        int stock,
        int warehouseId,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (string.IsNullOrWhiteSpace(sku))
            return;

        var updatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Update Products table (last stock)
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE Products
                SET LastStock = @stock,
                    LastWarehouseId = @warehouseId,
                    LastStockUpdatedAt = @updatedAt
                WHERE Sku = @sku;
                """;

            command.Parameters.AddWithValue("@sku", sku);
            command.Parameters.AddWithValue("@stock", stock);
            command.Parameters.AddWithValue("@warehouseId", warehouseId);
            command.Parameters.AddWithValue("@updatedAt", updatedAt);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Upsert into ProductStocks table (per warehouse)
        await using (var upsertCommand = connection.CreateCommand())
        {
            upsertCommand.CommandText =
                """
                INSERT INTO ProductStocks (Sku, WarehouseId, Stock, UpdatedAt)
                VALUES (@sku, @warehouseId, @stock, @updatedAt)
                ON CONFLICT(Sku, WarehouseId) DO UPDATE SET
                    Stock = excluded.Stock,
                    UpdatedAt = excluded.UpdatedAt;
                """;

            upsertCommand.Parameters.AddWithValue("@sku", sku);
            upsertCommand.Parameters.AddWithValue("@warehouseId", warehouseId);
            upsertCommand.Parameters.AddWithValue("@stock", stock);
            upsertCommand.Parameters.AddWithValue("@updatedAt", updatedAt);

            await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ProductStockItem>> GetProductStocksAsync(
        string sku,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (string.IsNullOrWhiteSpace(sku))
            return Array.Empty<ProductStockItem>();

        var stocks = new List<ProductStockItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Sku, WarehouseId, Stock, UpdatedAt
            FROM ProductStocks
            WHERE Sku = @sku
            ORDER BY WarehouseId;
            """;
        command.Parameters.AddWithValue("@sku", sku);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var updatedAtStr = reader.GetString(3);
            var updatedAt = DateTimeOffset.TryParse(updatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            stocks.Add(new ProductStockItem
            {
                Sku = reader.GetString(0),
                WarehouseId = reader.GetInt32(1),
                Stock = reader.GetInt32(2),
                UpdatedAt = updatedAt
            });
        }

        return stocks;
    }

    public async Task<IReadOnlyList<CustomerListItem>> GetCreatedCustomersAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        var customers = new List<CustomerListItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.ActindoCustomerId,
                   c.JobId,
                   c.DebtorNumber,
                   c.Name,
                   c.CreatedAt
            FROM Customers c
            ORDER BY c.UpdatedAt DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var createdAtStr = reader.IsDBNull(4) ? null : reader.GetString(4);
            DateTimeOffset? createdAt = null;
            if (createdAtStr != null && DateTimeOffset.TryParse(createdAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }

            customers.Add(new CustomerListItem
            {
                JobId = Guid.Parse(reader.GetString(1)),
                CustomerId = reader.GetInt32(0),
                DebtorNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                CreatedAt = createdAt
            });
        }

        return customers;
    }

    public async Task SaveCustomerAsync(
        Guid jobId,
        int actindoCustomerId,
        string debtorNumber,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Customers (ActindoCustomerId, JobId, DebtorNumber, Name, CreatedAt, UpdatedAt)
            VALUES (@actindoCustomerId, @jobId, @debtorNumber, @name, @now, @now)
            ON CONFLICT(ActindoCustomerId) DO UPDATE SET
                JobId = excluded.JobId,
                DebtorNumber = excluded.DebtorNumber,
                Name = excluded.Name,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("@actindoCustomerId", actindoCustomerId);
        command.Parameters.AddWithValue("@jobId", jobId.ToString());
        command.Parameters.AddWithValue("@debtorNumber", debtorNumber ?? string.Empty);
        command.Parameters.AddWithValue("@name", name ?? string.Empty);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureDatabase()
    {
        if (_initialized)
            return;

        lock (_initializationLock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(GetDatabasePath())!);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Migration: if JobEvents still exists from old schema, rebuild Products without FK and drop old tables
            using var migrationCheck = connection.CreateCommand();
            migrationCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='JobEvents';";
            if (Convert.ToInt32(migrationCheck.ExecuteScalar()) > 0)
            {
                _logger.LogInformation("Migrating database: removing JobEvents FK from Products table");
                using var migrationTx = connection.BeginTransaction();
                try
                {
                    ExecuteNonQuery(connection, migrationTx,
                        """
                        CREATE TABLE Products_new
                        (
                            Id TEXT PRIMARY KEY NOT NULL,
                            JobId TEXT NOT NULL,
                            ActindoProductId INTEGER NULL,
                            Sku TEXT NOT NULL,
                            Name TEXT NOT NULL DEFAULT '',
                            VariantStatus TEXT NOT NULL DEFAULT 'single',
                            ParentSku TEXT NULL,
                            VariantCode TEXT NULL,
                            CreatedAt TEXT NOT NULL,
                            LastPrice REAL NULL,
                            LastPriceEmployee REAL NULL,
                            LastPriceMember REAL NULL,
                            LastStock INTEGER NULL,
                            LastWarehouseId INTEGER NULL,
                            LastPriceUpdatedAt TEXT NULL,
                            LastStockUpdatedAt TEXT NULL
                        )
                        """);
                    ExecuteNonQuery(connection, migrationTx,
                        """
                        INSERT INTO Products_new
                            SELECT Id, JobId, ActindoProductId, Sku, Name, VariantStatus, ParentSku, VariantCode, CreatedAt,
                                   NULL, NULL, NULL, NULL, NULL, NULL, NULL
                            FROM Products
                        """);
                    ExecuteNonQuery(connection, migrationTx, "DROP TABLE Products");
                    ExecuteNonQuery(connection, migrationTx, "ALTER TABLE Products_new RENAME TO Products");
                    ExecuteNonQuery(connection, migrationTx, "DROP TABLE IF EXISTS JobActindoLogs");
                    ExecuteNonQuery(connection, migrationTx, "DROP TABLE IF EXISTS JobEvents");
                    migrationTx.Commit();
                    _logger.LogInformation("Database migration completed successfully");
                }
                catch (Exception ex)
                {
                    migrationTx.Rollback();
                    _logger.LogError(ex, "Database migration failed");
                    throw;
                }
            }

            // Products table for storing created products with variants
            using var productsCommand = connection.CreateCommand();
            productsCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Products
                (
                    Id TEXT PRIMARY KEY NOT NULL,
                    JobId TEXT NOT NULL,
                    ActindoProductId INTEGER NULL,
                    Sku TEXT NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    VariantStatus TEXT NOT NULL DEFAULT 'single',
                    ParentSku TEXT NULL,
                    VariantCode TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );
                """;
            productsCommand.ExecuteNonQuery();

            using var productsSkuIndex = connection.CreateCommand();
            productsSkuIndex.CommandText =
                """
                CREATE INDEX IF NOT EXISTS IX_Products_Sku
                    ON Products (Sku);
                """;
            productsSkuIndex.ExecuteNonQuery();

            using var productsParentIndex = connection.CreateCommand();
            productsParentIndex.CommandText =
                """
                CREATE INDEX IF NOT EXISTS IX_Products_ParentSku
                    ON Products (ParentSku);
                """;
            productsParentIndex.ExecuteNonQuery();

            // Preis- und Bestandsfelder
            EnsureColumn(connection, "Products", "LastPrice", "REAL NULL");
            EnsureColumn(connection, "Products", "LastPriceEmployee", "REAL NULL");
            EnsureColumn(connection, "Products", "LastPriceMember", "REAL NULL");
            EnsureColumn(connection, "Products", "LastStock", "INTEGER NULL");
            EnsureColumn(connection, "Products", "LastWarehouseId", "INTEGER NULL");
            EnsureColumn(connection, "Products", "LastPriceUpdatedAt", "TEXT NULL");
            EnsureColumn(connection, "Products", "LastStockUpdatedAt", "TEXT NULL");

            // ProductStocks Tabelle für Lagerbestände pro Lager
            using var stocksCommand = connection.CreateCommand();
            stocksCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS ProductStocks
                (
                    Sku TEXT NOT NULL,
                    WarehouseId INTEGER NOT NULL,
                    Stock INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (Sku, WarehouseId)
                );
                """;
            stocksCommand.ExecuteNonQuery();

            using var stocksSkuIndex = connection.CreateCommand();
            stocksSkuIndex.CommandText =
                """
                CREATE INDEX IF NOT EXISTS IX_ProductStocks_Sku
                    ON ProductStocks (Sku);
                """;
            stocksSkuIndex.ExecuteNonQuery();

            // Customers table for storing created/saved customers
            using var customersCommand = connection.CreateCommand();
            customersCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Customers
                (
                    ActindoCustomerId INTEGER PRIMARY KEY NOT NULL,
                    JobId TEXT NOT NULL,
                    DebtorNumber TEXT NOT NULL DEFAULT '',
                    Name TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            customersCommand.ExecuteNonQuery();

            using var customersDebtorIndex = connection.CreateCommand();
            customersDebtorIndex.CommandText =
                """
                CREATE INDEX IF NOT EXISTS IX_Customers_DebtorNumber
                    ON Customers (DebtorNumber);
                """;
            customersDebtorIndex.ExecuteNonQuery();

            _initialized = true;
        }
    }

    private string GetDatabasePath()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        return builder.DataSource;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = $"PRAGMA table_info({table});";

        using var reader = pragmaCommand.ExecuteReader();
        while (reader.Read())
        {
            var columnName = reader.GetString(1);
            if (string.Equals(columnName, column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alterCommand.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar ?? 0);
    }

    private static MetricSnapshot BuildMetricSnapshot(int total, IEnumerable<ProductJobInfo> jobs)
    {
        var relevantJobs = jobs.ToList();
        var successfulJobs = relevantJobs.Count(job => job.Status == ProductSyncJobStatus.Completed);
        var failedJobs = relevantJobs.Count(job => job.Status == ProductSyncJobStatus.Failed);

        var completedDurations = relevantJobs
            .Where(job =>
                job.StartedAt.HasValue &&
                job.CompletedAt.HasValue &&
                job.CompletedAt >= job.StartedAt)
            .Select(job => (job.CompletedAt!.Value - job.StartedAt!.Value).TotalSeconds)
            .ToList();

        return new MetricSnapshot
        {
            Total = total,
            Success = successfulJobs,
            Failed = failedJobs,
            AverageDurationSeconds = completedDurations.Count == 0
                ? 0
                : completedDurations.Average()
        };
    }

    private static bool IsProductOperation(string operation) =>
        operation is "create" or "save" or "full" or "inventory" or "price";

    private static bool IsCustomerOperation(string operation) =>
        operation is "customer-create" or "customer-save";

    private static bool IsMediaOperation(string operation) =>
        operation is "image-upload";

    private static bool IsTransactionOperation(string operation) =>
        operation.Contains("transaction", StringComparison.OrdinalIgnoreCase);

    private static string BuildConnectionString(string? configured, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured);

        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.Combine(contentRoot, builder.DataSource);
        }

        return builder.ToString();
    }
}
