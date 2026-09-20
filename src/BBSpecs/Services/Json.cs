using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBSpecs.Services;

/// <summary>
/// JSON settings for the payload we hand to the UI.
/// Sensors occasionally hand back NaN or infinity. Those aren't legal JSON and
/// would either throw here or arrive in the page as a string, so we write them
/// as null and let the front-end render its "not available" dash.
/// </summary>
public static class Json
{
    private sealed class SafeDouble : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? 0 : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }

    private sealed class SafeNullableDouble : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value is double d && double.IsFinite(d)) writer.WriteNumberValue(d);
            else writer.WriteNullValue();
        }
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new SafeDouble(), new SafeNullableDouble() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
