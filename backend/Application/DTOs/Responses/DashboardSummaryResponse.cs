using System;
using System.Text.Json.Serialization;

namespace ActindoMiddleware.DTOs.Responses;

public sealed class DashboardSummaryResponse
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Environment { get; init; } = "Production";
    public int ActiveJobs { get; init; }
    public DashboardMetricCard Products { get; init; } = new() { Title = "Produkte" };
    public DashboardMetricCard Customers { get; init; } = new() { Title = "Kunden" };
    public DashboardMetricCard Transactions { get; init; } = new() { Title = "Transaktionen" };
    public DashboardMetricCard Media { get; init; } = new() { Title = "Medien" };

    [JsonPropertyName("oauth")]
    public OAuthStatusDto OAuth { get; init; } = new();

    [JsonPropertyName("actindo")]
    public ActindoStatusDto Actindo { get; init; } = new();
}

public sealed class DashboardMetricCard
{
    public string Title { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Success { get; init; }
    public int Failed { get; init; }
    public double AverageDurationSeconds { get; init; }
}

public sealed class OAuthStatusDto
{
    public string State { get; init; } = "unknown";
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool HasRefreshToken { get; init; }
}

public sealed class ActindoStatusDto
{
    public string State { get; init; } = "unknown";
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
}
