using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ISettingsStore settingsStore,
        IActindoEndpointProvider endpointProvider,
        IAuthenticationService authenticationService,
        IHttpClientFactory httpClientFactory,
        ILogger<SettingsController> logger)
    {
        _settingsStore = settingsStore;
        _endpointProvider = endpointProvider;
        _authenticationService = authenticationService;
        _httpClientFactory = httpClientFactory;
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

    [HttpPost("actindo/validate-tokens")]
    public async Task<ActionResult<ActindoTokenValidationResponse>> ValidateActindoTokens(
        [FromBody] ActindoSettingsDto payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();

        var accessTokenResult = await ValidateAccessTokenAsync(client, payload, cancellationToken);
        var refreshTokenResult = await ValidateRefreshTokenAsync(client, payload, cancellationToken);

        return Ok(new ActindoTokenValidationResponse
        {
            AccessToken = accessTokenResult,
            RefreshToken = refreshTokenResult
        });
    }

    [HttpPost("actindo/validate-nav")]
    public async Task<ActionResult<NavApiValidationResponse>> ValidateNavApi(
        [FromBody] ActindoSettingsDto payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var navApiResult = await ValidateNavApiAsync(client, payload, cancellationToken);

        return Ok(new NavApiValidationResponse
        {
            NavApi = navApiResult
        });
    }

    private static async Task<TokenValidationResult> ValidateAccessTokenAsync(
        HttpClient client,
        ActindoSettingsDto payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Kein Access-Token eingetragen."
            };
        }

        var baseUrl = NormalizeBaseUrl(payload.ActindoBaseUrl);
        var endpointValue = payload.Endpoints != null &&
                            payload.Endpoints.TryGetValue("GET_PRODUCT_LIST", out var configuredEndpoint)
            ? configuredEndpoint
            : string.Empty;
        var endpoint = BuildAbsoluteUrl(baseUrl, endpointValue);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "GET_PRODUCT_LIST Endpoint oder Actindo Base URL fehlt."
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? new TokenValidationResult
                {
                    Valid = true,
                    Message = "Access-Token ist gueltig."
                }
                : new TokenValidationResult
                {
                    Valid = false,
                    Message = string.IsNullOrWhiteSpace(body)
                        ? $"Access-Token ungueltig ({(int)response.StatusCode})."
                        : $"Access-Token ungueltig ({(int)response.StatusCode}): {TrimMessage(body)}"
                };
        }
        catch (Exception ex)
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = $"Access-Token konnte nicht geprueft werden: {TrimMessage(ex.Message)}"
            };
        }
    }

    private static async Task<TokenValidationResult> ValidateRefreshTokenAsync(
        HttpClient client,
        ActindoSettingsDto payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Kein Refresh-Token eingetragen."
            };
        }

        if (string.IsNullOrWhiteSpace(payload.ClientId) || string.IsNullOrWhiteSpace(payload.ClientSecret))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Client ID oder Client Secret fehlt."
            };
        }

        var tokenEndpoint = BuildAbsoluteUrl(NormalizeBaseUrl(payload.ActindoBaseUrl), payload.TokenEndpoint);
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Token Endpoint fehlt."
            };
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = payload.ClientId,
            ["client_secret"] = payload.ClientSecret,
            ["refresh_token"] = payload.RefreshToken
        });

        try
        {
            using var response = await client.PostAsync(tokenEndpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? new TokenValidationResult
                {
                    Valid = true,
                    Message = "Refresh-Token ist gueltig."
                }
                : new TokenValidationResult
                {
                    Valid = false,
                    Message = string.IsNullOrWhiteSpace(body)
                        ? $"Refresh-Token ungueltig ({(int)response.StatusCode})."
                        : $"Refresh-Token ungueltig ({(int)response.StatusCode}): {TrimMessage(body)}"
                };
        }
        catch (Exception ex)
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = $"Refresh-Token konnte nicht geprueft werden: {TrimMessage(ex.Message)}"
            };
        }
    }

    private static async Task<TokenValidationResult> ValidateNavApiAsync(
        HttpClient client,
        ActindoSettingsDto payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.NavApiUrl))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Keine NAV API URL eingetragen."
            };
        }

        if (string.IsNullOrWhiteSpace(payload.NavApiToken))
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = "Kein NAV API Token eingetragen."
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, payload.NavApiUrl)
        {
            Content = JsonContent.Create(new { requestType = "actindo.product.ids.get" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", payload.NavApiToken);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new TokenValidationResult
                {
                    Valid = false,
                    Message = string.IsNullOrWhiteSpace(body)
                        ? $"NAV API nicht erreichbar ({(int)response.StatusCode})."
                        : $"NAV API nicht erreichbar ({(int)response.StatusCode}): {TrimMessage(body)}"
                };
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("success", out var successProp) &&
                    successProp.ValueKind == JsonValueKind.False)
                {
                    var error = document.RootElement.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString()
                        : null;
                    return new TokenValidationResult
                    {
                        Valid = false,
                        Message = string.IsNullOrWhiteSpace(error)
                            ? "NAV API hat success=false zurueckgegeben."
                            : $"NAV API hat success=false zurueckgegeben: {TrimMessage(error)}"
                    };
                }
            }
            catch
            {
                // non-json response is still enough to prove connectivity/auth as long as status is 2xx
            }

            return new TokenValidationResult
            {
                Valid = true,
                Message = "NAV API ist erreichbar und der Bearer-Token scheint gueltig zu sein."
            };
        }
        catch (Exception ex)
        {
            return new TokenValidationResult
            {
                Valid = false,
                Message = $"NAV API konnte nicht geprueft werden: {TrimMessage(ex.Message)}"
            };
        }
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        var baseUrl = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    private static string BuildAbsoluteUrl(string? baseUrl, string? value)
    {
        var endpoint = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (string.IsNullOrWhiteSpace(baseUrl))
            return endpoint.TrimStart('/');

        return $"{baseUrl}{endpoint.TrimStart('/')}";
    }

    private static string TrimMessage(string message)
    {
        var trimmed = (message ?? string.Empty).Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180];
    }
}
