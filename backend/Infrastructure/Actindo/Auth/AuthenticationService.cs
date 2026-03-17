using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Infrastructure.Actindo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActindoMiddleware.Infrastructure.Actindo.Auth;

public interface IAuthenticationService
{
    Task<OAuthToken> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken ct = default);
    Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<string> GetValidAccessTokenAsync(CancellationToken ct = default);
    void ClearTokens();
    Task ClearTokensAsync(CancellationToken ct = default);
    OAuthStatusSnapshot GetStatusSnapshot();
    void InvalidateCache();
    Task CheckAvailabilityAsync(CancellationToken ct = default);
}

public sealed class AuthenticationService : IAuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly IActindoAvailabilityTracker _availabilityTracker;
    private readonly ISettingsStore _settingsStore;
    private readonly ActindoOAuthOptions _oauthDefaults;
    private string? _clientId;
    private string? _clientSecret;
    private Uri? _tokenEndpoint;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly ILogger<AuthenticationService> _logger;

    private OAuthToken? _currentToken;
    private string? _lastErrorMessage;
    private DateTimeOffset? _lastErrorAt;
    private DateTimeOffset? _lastRefreshAttemptAt;
    private bool _configLoaded;

    public AuthenticationService(
        HttpClient httpClient,
        IOptions<ActindoOAuthOptions> options,
        IActindoAvailabilityTracker availabilityTracker,
        ISettingsStore settingsStore,
        ILogger<AuthenticationService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _oauthDefaults = options.Value ?? new ActindoOAuthOptions();
        _availabilityTracker = availabilityTracker;
        _logger = logger;
    }

    public async Task<OAuthToken> ExchangeAuthorizationCodeAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        await EnsureConfigAsync(ct);

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirectUri,
            ["code"] = code
        };

        using var content = new FormUrlEncodedContent(values);
        _logger.LogInformation("Exchange auth code at {Endpoint}", _tokenEndpoint);
        try
        {
            using var response = await _httpClient.PostAsync(_tokenEndpoint, content, ct).ConfigureAwait(false);
            var rawContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                SetLastError($"Token exchange failed ({(int)response.StatusCode}): {rawContent}");
                _availabilityTracker.ReportFailure(new InvalidOperationException(rawContent));
                throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {rawContent}");
            }

            var token = DeserializeToken(rawContent);
            token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60);
            _currentToken = token;
            ClearLastError();
            _availabilityTracker.ReportSuccess();
            _logger.LogInformation("Authorization code exchange succeeded. ExpiresIn: {ExpiresIn}", token.ExpiresIn);

            if (!string.IsNullOrEmpty(token.RefreshToken))
            {
                await PersistTokensAsync(token.RefreshToken, token.AccessToken, token.ExpiresAt, ct);
            }

            return token;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            _availabilityTracker.ReportFailure(ex);
            throw;
        }
    }

    public async Task<OAuthToken> RefreshAsync(
        string refreshToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        await EnsureConfigAsync(ct);

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["refresh_token"] = refreshToken
        };

        using var content = new FormUrlEncodedContent(values);
        _logger.LogInformation("Refreshing access token at {Endpoint}", _tokenEndpoint);
        try
        {
            using var response = await _httpClient.PostAsync(_tokenEndpoint, content, ct).ConfigureAwait(false);
            var rawContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                SetLastError($"Token refresh failed ({(int)response.StatusCode}): {rawContent}");
                _availabilityTracker.ReportFailure(new InvalidOperationException(rawContent));
                throw new InvalidOperationException($"Token refresh failed ({(int)response.StatusCode}): {rawContent}");
            }

            var token = DeserializeToken(rawContent);
            token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60);

            if (!string.IsNullOrEmpty(token.RefreshToken))
            {
                await PersistTokensAsync(token.RefreshToken, token.AccessToken, token.ExpiresAt, ct);
            }
            else
            {
                token.RefreshToken = refreshToken;
                await PersistTokensAsync(refreshToken, token.AccessToken, token.ExpiresAt, ct);
            }

            _currentToken = token;
            ClearLastError();
            _availabilityTracker.ReportSuccess();
            _logger.LogInformation("Token refresh succeeded. New expiry in {ExpiresIn} seconds.", token.ExpiresIn);
            return token;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            _availabilityTracker.ReportFailure(ex);
            throw;
        }
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await EnsureConfigAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var shouldRefresh =
                _currentToken == null ||
                _currentToken.ExpiresAt == null ||
                now >= _currentToken.ExpiresAt.Value.AddMinutes(-2);

            var recentlyChecked = _lastRefreshAttemptAt.HasValue &&
                                  now - _lastRefreshAttemptAt.Value <= TimeSpan.FromMinutes(2);

            if (!shouldRefresh && recentlyChecked)
            {
                return _currentToken!.AccessToken!;
            }

            if (shouldRefresh)
            {
                if (string.IsNullOrWhiteSpace(_currentToken?.RefreshToken))
                {
                    await ReloadTokenFromSettingsAsync(ct);
                }

                var refreshToken = _currentToken?.RefreshToken;

                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    _logger.LogInformation("Refreshing token because current one is missing or expiring.");
                    _lastRefreshAttemptAt = now;
                    await RefreshAsync(refreshToken!, ct).ConfigureAwait(false);
                }
                else
                {
                    _lastRefreshAttemptAt = now;
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        if (_currentToken?.AccessToken == null)
        {
            throw new InvalidOperationException(
                "No access token available. Initialize the integration via ExchangeAuthorizationCodeAsync first.");
        }

        return _currentToken.AccessToken;
    }

    public OAuthStatusSnapshot GetStatusSnapshot()
    {
        var token = _currentToken;
        return new OAuthStatusSnapshot
        {
            HasAccessToken = !string.IsNullOrWhiteSpace(token?.AccessToken),
            HasRefreshToken = !string.IsNullOrWhiteSpace(token?.RefreshToken),
            AccessTokenExpiresAt = token?.ExpiresAt,
            LastErrorAt = _lastErrorAt,
            LastErrorMessage = _lastErrorMessage
        };
    }

    public async Task ClearTokensAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("Clearing Actindo OAuth tokens.");
        _currentToken = null;
        ClearLastError();

        // Nur Tokens löschen, andere Settings behalten
        var existingSettings = await _settingsStore.GetActindoSettingsAsync(ct);
        var clearedSettings = new ActindoSettings
        {
            AccessToken = null,
            AccessTokenExpiresAt = null,
            RefreshToken = null,
            ClientId = existingSettings.ClientId,
            ClientSecret = existingSettings.ClientSecret,
            TokenEndpoint = existingSettings.TokenEndpoint,
            Endpoints = existingSettings.Endpoints,
            NavApiUrl = existingSettings.NavApiUrl,
            NavApiToken = existingSettings.NavApiToken,
            WarehouseMappings = existingSettings.WarehouseMappings
        };
        await _settingsStore.SaveActindoSettingsAsync(clearedSettings, ct);
    }

    public void ClearTokens()
    {
        _logger.LogWarning("Clearing Actindo OAuth tokens.");
        _currentToken = null;
        ClearLastError();
        // Synchrone Version - ruft async Version auf
        Task.Run(async () =>
        {
            try
            {
                var existingSettings = await _settingsStore.GetActindoSettingsAsync(CancellationToken.None);
                var clearedSettings = new ActindoSettings
                {
                    AccessToken = null,
                    AccessTokenExpiresAt = null,
                    RefreshToken = null,
                    ClientId = existingSettings.ClientId,
                    ClientSecret = existingSettings.ClientSecret,
                    TokenEndpoint = existingSettings.TokenEndpoint,
                    Endpoints = existingSettings.Endpoints,
                    NavApiUrl = existingSettings.NavApiUrl,
                    NavApiToken = existingSettings.NavApiToken,
                    WarehouseMappings = existingSettings.WarehouseMappings
                };
                await _settingsStore.SaveActindoSettingsAsync(clearedSettings, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear tokens from database.");
            }
        });
    }

    public void InvalidateCache()
    {
        _logger.LogInformation("InvalidateCache called - clearing all cached config and tokens");
        _configLoaded = false;
        _clientId = null;
        _clientSecret = null;
        _tokenEndpoint = null;
        _currentToken = null;
    }

    public async Task CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureConfigAsync(ct);
            if (_tokenEndpoint == null)
                return;

            using var request = new HttpRequestMessage(HttpMethod.Head, _tokenEndpoint);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Even a non-success status means the host is reachable.
                _availabilityTracker.ReportSuccess();
            }
            else
            {
                _availabilityTracker.ReportSuccess();
            }
        }
        catch (Exception ex)
        {
            _availabilityTracker.ReportFailure(ex);
        }
    }

    private OAuthToken DeserializeToken(string rawContent)
    {
        return JsonSerializer.Deserialize<OAuthToken>(rawContent, _serializerOptions)
               ?? throw new InvalidOperationException("Empty token JSON received from Actindo.");
    }

    private void SetLastError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            message = "Unknown OAuth error";

        _lastErrorAt = DateTimeOffset.UtcNow;
        _lastErrorMessage = message;
    }

    private void ClearLastError()
    {
        _lastErrorAt = null;
        _lastErrorMessage = null;
    }

    private async Task EnsureConfigAsync(CancellationToken cancellationToken)
    {
        if (_configLoaded)
        {
            _logger.LogDebug("Config already loaded, skipping");
            return;
        }

        await _configLock.WaitAsync(cancellationToken);
        try
        {
            if (_configLoaded)
                return;

            _logger.LogInformation("Loading OAuth config from settings store...");
            var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);

            _clientId = string.IsNullOrWhiteSpace(settings.ClientId) ? _oauthDefaults.ClientId : settings.ClientId;
            _clientSecret = string.IsNullOrWhiteSpace(settings.ClientSecret) ? _oauthDefaults.ClientSecret : settings.ClientSecret;
            var tokenEndpoint = string.IsNullOrWhiteSpace(settings.TokenEndpoint)
                ? _oauthDefaults.TokenEndpoint
                : settings.TokenEndpoint;
            _tokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? null : new Uri(tokenEndpoint);

            _logger.LogInformation("Loaded config: ClientId={ClientId}, TokenEndpoint={TokenEndpoint}, HasRefreshToken={HasRefresh}, HasAccessToken={HasAccess}",
                !string.IsNullOrWhiteSpace(_clientId) ? "(set)" : "(empty)",
                _tokenEndpoint?.ToString() ?? "(null)",
                !string.IsNullOrWhiteSpace(settings.RefreshToken),
                !string.IsNullOrWhiteSpace(settings.AccessToken));

            if (string.IsNullOrWhiteSpace(_clientId) ||
                string.IsNullOrWhiteSpace(_clientSecret) ||
                _tokenEndpoint == null)
            {
                _logger.LogWarning("OAuth configuration is incomplete - ClientId, ClientSecret, or TokenEndpoint missing");
                throw new InvalidOperationException("Actindo OAuth configuration is incomplete.");
            }

            if (!string.IsNullOrWhiteSpace(settings.AccessToken) || !string.IsNullOrWhiteSpace(settings.RefreshToken))
            {
                _currentToken = new OAuthToken
                {
                    AccessToken = settings.AccessToken,
                    RefreshToken = settings.RefreshToken,
                    ExpiresAt = settings.AccessTokenExpiresAt
                };
                _logger.LogInformation("Loaded tokens from settings: HasAccessToken={HasAccess}, HasRefreshToken={HasRefresh}",
                    !string.IsNullOrWhiteSpace(_currentToken.AccessToken),
                    !string.IsNullOrWhiteSpace(_currentToken.RefreshToken));
            }
            else
            {
                _logger.LogWarning("No tokens found in settings");
            }

            _configLoaded = true;
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task PersistTokensAsync(string? refreshToken, string? accessToken, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        settings = new ActindoSettings
        {
            AccessToken = accessToken,
            AccessTokenExpiresAt = expiresAt,
            RefreshToken = refreshToken,
            ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? _clientId : settings.ClientId,
            ClientSecret = string.IsNullOrWhiteSpace(settings.ClientSecret) ? _clientSecret : settings.ClientSecret,
            TokenEndpoint = string.IsNullOrWhiteSpace(settings.TokenEndpoint) ? _tokenEndpoint?.ToString() : settings.TokenEndpoint,
            Endpoints = settings.Endpoints,
            NavApiUrl = settings.NavApiUrl,
            NavApiToken = settings.NavApiToken,
            WarehouseMappings = settings.WarehouseMappings
        };

        await _settingsStore.SaveActindoSettingsAsync(settings, cancellationToken);
    }

    private async Task ReloadTokenFromSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.AccessToken) || !string.IsNullOrWhiteSpace(settings.RefreshToken))
        {
            _currentToken = new OAuthToken
            {
                AccessToken = settings.AccessToken,
                RefreshToken = settings.RefreshToken,
                ExpiresAt = settings.AccessTokenExpiresAt
            };
        }
    }
}
