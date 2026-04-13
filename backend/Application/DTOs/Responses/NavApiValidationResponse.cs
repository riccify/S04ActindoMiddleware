namespace ActindoMiddleware.DTOs.Responses;

public sealed class NavApiValidationResponse
{
    public required TokenValidationResult NavApi { get; init; }
}
