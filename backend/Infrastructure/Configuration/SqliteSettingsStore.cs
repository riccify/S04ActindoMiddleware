using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Infrastructure.Actindo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Infrastructure.Configuration;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private const string DefaultConnectionString = "Data Source=App_Data/dashboard.db";
    private const string ActindoSettingsKey = "actindo";
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly ILogger<SqliteSettingsStore> _logger;
    private bool _initialized;
    private readonly object _initGate = new();

    public SqliteSettingsStore(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<SqliteSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _connectionString = BuildConnectionString(
            configuration.GetConnectionString("Dashboard"),
            hostEnvironment.ContentRootPath);
        _logger.LogInformation("SQLite settings database: {ConnectionString}", _connectionString);
    }

    public async Task<ActindoSettings> GetActindoSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", ActindoSettingsKey);

        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return BuildDefaultSettings();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ActindoSettings>(value, _serializerOptions)
                        ?? BuildDefaultSettings();
            return NormalizeSettings(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Actindo settings JSON, using defaults.");
            return BuildDefaultSettings();
        }
    }

    public async Task SaveActindoSettingsAsync(ActindoSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = NormalizeSettings(settings);

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var json = JsonSerializer.Serialize(settings, _serializerOptions);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO Settings (Key, Value)
VALUES (@key, @value)
ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        command.Parameters.AddWithValue("@key", ActindoSettingsKey);
        command.Parameters.AddWithValue("@value", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        lock (_initGate)
        {
            if (_initialized)
                return;
            _initialized = true;
        }

        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            var directory = Path.GetDirectoryName(dataSource);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS Settings (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ActindoSettings BuildDefaultSettings()
    {
        return new ActindoSettings
        {
            Endpoints = BuildDefaultEndpoints()
        };
    }

    private static ActindoSettings NormalizeSettings(ActindoSettings source)
    {
        return new ActindoSettings
        {
            AccessToken = source.AccessToken,
            AccessTokenExpiresAt = source.AccessTokenExpiresAt,
            RefreshToken = source.RefreshToken,
            TokenEndpoint = source.TokenEndpoint,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            ActindoBaseUrl = NormalizeBaseUrl(source.ActindoBaseUrl),
            Endpoints = MergeWithDefaults(source.Endpoints, source.ActindoBaseUrl),
            NavApiUrl = source.NavApiUrl,
            NavApiToken = source.NavApiToken,
            WarehouseMappings = source.WarehouseMappings ?? new()
        };
    }

    private static Dictionary<string, string> BuildDefaultEndpoints() => new()
    {
        ["CREATE_PRODUCT"] = ActindoEndpoints.CREATE_PRODUCT,
        ["SAVE_PRODUCT"] = ActindoEndpoints.SAVE_PRODUCT,
        ["CREATE_INVENTORY"] = ActindoEndpoints.CREATE_INVENTORY,
        ["CREATE_INVENTORY_MOVEMENT"] = ActindoEndpoints.CREATE_INVENTORY_MOVEMENT,
        ["CREATE_RELATION"] = ActindoEndpoints.CREATE_RELATION,
        ["CREATE_CUSTOMER"] = ActindoEndpoints.CREATE_CUSTOMER,
        ["SAVE_CUSTOMER"] = ActindoEndpoints.SAVE_CUSTOMER,
        ["SAVE_PRIMARY_ADDRESS"] = ActindoEndpoints.SAVE_PRIMARY_ADDRESS,
        ["GET_TRANSACTIONS"] = ActindoEndpoints.GET_TRANSACTIONS,
        ["CREATE_FILE"] = ActindoEndpoints.CREATE_FILE,
        ["PRODUCT_FILES_SAVE"] = ActindoEndpoints.PRODUCT_FILES_SAVE,
        ["GET_PRODUCT_LIST"] = ActindoEndpoints.GET_PRODUCT_LIST,
        ["DELETE_PRODUCT"] = ActindoEndpoints.DELETE_PRODUCT,
        ["GET_PRODUCT"] = ActindoEndpoints.GET_PRODUCT,
        ["GET_VARIANTS_LIST"] = ActindoEndpoints.GET_VARIANTS_LIST
    };

    private static Dictionary<string, string> MergeWithDefaults(IDictionary<string, string>? current, string? actindoBaseUrl)
    {
        var merged = new Dictionary<string, string>(BuildDefaultEndpoints(), StringComparer.OrdinalIgnoreCase);
        if (current != null)
        {
            foreach (var kvp in current)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    continue;
                merged[kvp.Key] = NormalizeEndpointValue(kvp.Value);
            }
        }

        return merged;
    }

    private static string? NormalizeBaseUrl(string? baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string NormalizeEndpointValue(string? value)
    {
        var endpoint = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absoluteEndpoint))
        {
            return absoluteEndpoint.AbsolutePath.TrimStart('/');
        }

        return endpoint.TrimStart('/');
    }

    private static string BuildConnectionString(string? configured, string contentRoot)
    {
        var resolved = string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured;
        var builder = new SqliteConnectionStringBuilder(resolved);

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            builder.DataSource = "App_Data/dashboard.db";

        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.Combine(contentRoot, builder.DataSource);
        }

        return builder.ToString();
    }
}
