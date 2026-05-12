namespace ActindoMiddleware.DTOs.Requests;

public sealed class GetTransactionsRequest
{
    public string Action { get; init; } = "from";

    public required string Date { get; init; }
}
