namespace ActindoMiddleware.DTOs.Requests;

public sealed class LogReplayRequest
{
    public string Endpoint { get; init; } = string.Empty;
    public string RequestPayload { get; init; } = string.Empty;
}
