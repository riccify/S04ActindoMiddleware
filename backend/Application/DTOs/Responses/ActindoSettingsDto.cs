using System;
using System.Collections.Generic;

namespace ActindoMiddleware.DTOs.Responses;

public sealed class ActindoSettingsDto
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
    public Dictionary<string, int> WarehouseMappings { get; init; } = new();

    // Base URL for Actindo frontend links
    public string? ActindoBaseUrl { get; init; }
}

