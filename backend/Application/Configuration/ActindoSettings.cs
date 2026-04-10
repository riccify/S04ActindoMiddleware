using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ActindoMiddleware.Infrastructure.Serialization;

namespace ActindoMiddleware.Application.Configuration;

public sealed class ActindoSettings
{
    public string? AccessToken { get; init; }
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }
    public string? RefreshToken { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public Dictionary<string, string> Endpoints { get; init; } = new();

    // NAV API settings
    public string? NavApiUrl { get; init; }
    public string? NavApiToken { get; init; }

    // Warehouse name → Actindo ID mappings
    [JsonConverter(typeof(StringOrNumberIntDictionaryConverter))]
    public Dictionary<string, int> WarehouseMappings { get; init; } = new();

    // Base URL for Actindo frontend links (e.g. https://schalke-dev.dev.actindo.com)
    public string? ActindoBaseUrl { get; init; }
}

