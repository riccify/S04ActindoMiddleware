using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize(Policy = AuthPolicies.Admin)]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IActindoEndpointProvider _endpointProvider;
    private readonly IAuthenticationService _authenticationService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ISettingsStore settingsStore,
        IActindoEndpointProvider endpointProvider,
        IAuthenticationService authenticationService,
        ILogger<SettingsController> logger)
    {
        _settingsStore = settingsStore;
        _endpointProvider = endpointProvider;
        _authenticationService = authenticationService;
        _logger = logger;
    }

    [HttpGet("actindo")]
    public async Task<ActionResult<ActindoSettingsDto>> GetActindoSettings(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GET /api/settings/actindo - Loading settings");
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        _logger.LogInformation("Settings loaded: ClientId={ClientId}, TokenEndpoint={TokenEndpoint}, HasRefreshToken={HasRefresh}, HasAccessToken={HasAccess}",
            settings.ClientId ?? "(null)",
            settings.TokenEndpoint ?? "(null)",
            !string.IsNullOrEmpty(settings.RefreshToken),
            !string.IsNullOrEmpty(settings.AccessToken));
        return Ok(new ActindoSettingsDto
        {
            AccessToken = settings.AccessToken,
            AccessTokenExpiresAt = settings.AccessTokenExpiresAt,
            RefreshToken = settings.RefreshToken,
            TokenEndpoint = settings.TokenEndpoint,
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            Endpoints = settings.Endpoints ?? new(),
            NavApiUrl = settings.NavApiUrl,
            NavApiToken = settings.NavApiToken,
            WarehouseMappings = settings.WarehouseMappings ?? new(),
            ActindoBaseUrl = settings.ActindoBaseUrl
        });
    }

    [HttpPut("actindo")]
    public async Task<IActionResult> SaveActindoSettings([FromBody] ActindoSettingsDto payload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("PUT /api/settings/actindo - Saving settings");
        _logger.LogInformation("Received: ClientId={ClientId}, TokenEndpoint={TokenEndpoint}, HasRefreshToken={HasRefresh}",
            payload.ClientId ?? "(null)",
            payload.TokenEndpoint ?? "(null)",
            !string.IsNullOrEmpty(payload.RefreshToken));

        var toSave = new ActindoSettings
        {
            AccessToken = payload.AccessToken,
            AccessTokenExpiresAt = payload.AccessTokenExpiresAt,
            RefreshToken = payload.RefreshToken,
            TokenEndpoint = payload.TokenEndpoint,
            ClientId = payload.ClientId,
            ClientSecret = payload.ClientSecret,
            Endpoints = payload.Endpoints ?? new(),
            NavApiUrl = payload.NavApiUrl,
            NavApiToken = payload.NavApiToken,
            WarehouseMappings = payload.WarehouseMappings ?? new(),
            ActindoBaseUrl = payload.ActindoBaseUrl
        };

        await _settingsStore.SaveActindoSettingsAsync(toSave, cancellationToken);
        _logger.LogInformation("Settings saved successfully, invalidating caches");
        _endpointProvider.Invalidate();
        _authenticationService.InvalidateCache();
        return NoContent();
    }
}
