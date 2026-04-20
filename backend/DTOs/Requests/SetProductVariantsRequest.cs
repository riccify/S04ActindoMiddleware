namespace ActindoMiddleware.DTOs.Requests;

public sealed class SetProductVariantsRequest
{
    public required int ProductId { get; init; }
    public int VariantSetId { get; init; } = 21;
    public required List<int> ChildrenIds { get; init; }
}
