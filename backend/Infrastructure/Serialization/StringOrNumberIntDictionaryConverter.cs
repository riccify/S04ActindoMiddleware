using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ActindoMiddleware.Infrastructure.Serialization;

/// <summary>
/// Accepts dictionary values as JSON numbers or numeric strings.
/// This keeps warehouse mappings stable even when the client sends IDs as strings.
/// </summary>
public sealed class StringOrNumberIntDictionaryConverter : JsonConverter<Dictionary<string, int>>
{
    public override Dictionary<string, int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new Dictionary<string, int>();

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Cannot convert {reader.TokenType} to Dictionary<string, int>.");

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return result;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name while reading warehouse mappings.");

            var key = reader.GetString();
            if (string.IsNullOrWhiteSpace(key))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON while reading warehouse mapping value.");

            result[key] = reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetInt32(),
                JsonTokenType.String when int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => throw new JsonException($"Cannot convert {reader.TokenType} to int for warehouse mapping '{key}'.")
            };
        }

        throw new JsonException("Unexpected end of JSON while reading warehouse mappings.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, int> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (key, mappedId) in value)
        {
            writer.WriteNumber(key, mappedId);
        }

        writer.WriteEndObject();
    }
}
