using System;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Infrastructure.Actindo;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = AuthPolicies.Read)]
public sealed class DashboardController : ControllerBase
{
    // Use a very large window to show "all-time" statistics instead of just last 24h
    private static readonly TimeSpan SummaryWindow = TimeSpan.FromDays(365 * 100);
    private readonly IDashboardMetricsService _metricsService;
    private readonly IAuthenticationService _authenticationService;
    private readonly IActindoAvailabilityTracker _availabilityTracker;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IDashboardMetricsService metricsService,
        IAuthenticationService authenticationService,
        IActindoAvailabilityTracker availabilityTracker,
        IWebHostEnvironment hostEnvironment,
        ILogger<DashboardController> logger)
    {
        _metricsService = metricsService;
        _authenticationService = authenticationService;
        _availabilityTracker = availabilityTracker;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(DashboardSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummaryResponse>> GetSummary(
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("GET /api/dashboard/summary - Checking token and availability");
        try
        {
            var token = await _authenticationService.GetValidAccessTokenAsync(cancellationToken);
            _logger.LogInformation("Got valid access token (length={Length})", token?.Length ?? 0);
            await _authenticationService.CheckAvailabilityAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get valid access token or check availability: {Message}", ex.Message);
            // Keep the summary endpoint responsive even if Actindo is down.
        }

        var metricsSnapshot = await _metricsService.GetSnapshotAsync(
            SummaryWindow,
            cancellationToken);
        var oauthSnapshot = _authenticationService.GetStatusSnapshot();
        var actindoSnapshot = _availabilityTracker.GetSnapshot();

        _logger.LogInformation("OAuth Status: HasAccess={HasAccess}, HasRefresh={HasRefresh}, ExpiresAt={ExpiresAt}, LastError={LastError}",
            oauthSnapshot.HasAccessToken,
            oauthSnapshot.HasRefreshToken,
            oauthSnapshot.AccessTokenExpiresAt,
            oauthSnapshot.LastErrorMessage ?? "(none)");
        _logger.LogInformation("Actindo Status: State={State}, Message={Message}", actindoSnapshot.State, actindoSnapshot.Message);

        var response = new DashboardSummaryResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Environment = _hostEnvironment.EnvironmentName,
            ActiveJobs = metricsSnapshot.ActiveJobs,
            Products = MapCard("Produkte", metricsSnapshot.ProductStats),
            Customers = MapCard("Kunden", metricsSnapshot.CustomerStats),
            Transactions = MapCard("Transaktionen", metricsSnapshot.TransactionStats),
            Media = MapCard("Medien", metricsSnapshot.MediaStats),
            OAuth = MapOAuth(oauthSnapshot),
            Actindo = MapActindo(actindoSnapshot)
        };

        return Ok(response);
    }

    private static DashboardMetricCard MapCard(string title, MetricSnapshot snapshot) => new()
    {
        Title = title,
        Total = snapshot.Total,
        Success = snapshot.Success,
        Failed = snapshot.Failed,
        AverageDurationSeconds = snapshot.AverageDurationSeconds
    };

    private static OAuthStatusDto MapOAuth(OAuthStatusSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LastErrorMessage))
        {
            var normalized = snapshot.LastErrorMessage!;
            var lower = normalized.ToLowerInvariant();
            var statusMessage = lower.Contains("invalid refresh token") || lower.Contains("invalid_grant")
                ? "Refresh-Token ungueltig - bitte neu verbinden"
                : normalized.Length > 120
                    ? normalized[..120]
                    : normalized;

            return new OAuthStatusDto
            {
                State = "error",
                Message = statusMessage,
                ExpiresAt = snapshot.AccessTokenExpiresAt,
                HasRefreshToken = snapshot.HasRefreshToken
            };
        }

        if (!snapshot.HasAccessToken)
        {
            if (snapshot.HasRefreshToken)
            {
                return new OAuthStatusDto
                {
                    State = "warning",
                    Message = "Access-Token wird initialisiert",
                    ExpiresAt = snapshot.AccessTokenExpiresAt,
                    HasRefreshToken = true
                };
            }

            return new OAuthStatusDto
            {
                State = "error",
                Message = "Kein Access-Token geladen",
                ExpiresAt = snapshot.AccessTokenExpiresAt,
                HasRefreshToken = snapshot.HasRefreshToken
            };
        }

        if (snapshot.AccessTokenExpiresAt is not { } expires)
        {
            return new OAuthStatusDto
            {
                State = "warning",
                Message = "Token-Laufzeit unbekannt",
                ExpiresAt = null,
                HasRefreshToken = snapshot.HasRefreshToken
            };
        }

        var remaining = expires - DateTimeOffset.UtcNow;
        var state = remaining <= TimeSpan.Zero
            ? "error"
            : remaining <= TimeSpan.FromMinutes(5)
                ? "warning"
                : "ok";

        var message = state switch
        {
            "error" => "Token abgelaufen",
            "warning" => $"Laeuft in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} Min ab",
            _ => $"Gueltig bis {expires:HH:mm} UTC"
        };

        return new OAuthStatusDto
        {
            State = state,
            Message = message,
            ExpiresAt = expires,
            HasRefreshToken = snapshot.HasRefreshToken
        };
    }

    private static ActindoStatusDto MapActindo(ActindoAvailabilitySnapshot snapshot)
    {
        return new ActindoStatusDto
        {
            State = snapshot.State,
            Message = snapshot.Message,
            LastSuccessAt = snapshot.LastSuccessAt,
            LastFailureAt = snapshot.LastFailureAt
        };
    }
}
