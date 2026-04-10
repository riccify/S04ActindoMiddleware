namespace ActindoMiddleware.DTOs.Responses;

public sealed class ActindoTokenValidationResponse
{
    public required TokenValidationResult AccessToken { get; init; }
    public required TokenValidationResult RefreshToken { get; init; }
}

public sealed class TokenValidationResult
{
    public bool Valid { get; init; }
    public string Message { get; init; } = string.Empty;
}
