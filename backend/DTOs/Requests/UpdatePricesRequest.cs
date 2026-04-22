using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActindoMiddleware.Infrastructure.Serialization;

namespace ActindoMiddleware.DTOs.Requests;

public sealed class UpdatePricesRequest
{
    [JsonPropertyName("await")]
    public bool Await { get; init; } = true;

    [JsonPropertyName("bufferId")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? BufferId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Payload { get; init; } = new();

    public JsonElement ToPayloadElement() => JsonSerializer.SerializeToElement(Payload);
}
