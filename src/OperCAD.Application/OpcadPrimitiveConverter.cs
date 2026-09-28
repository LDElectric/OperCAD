using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperCAD.Application;

/// <summary>
/// Desserializador polimórfico para <see cref="OpcadPrimitive"/>: lê o discriminador
/// "type" (ou "$type") do JSON e instancia a classe derivada correta (line, rect,
/// ellipse, circle, arc, polygon, polyline, text), preservando a GEOMETRIA real. Sem
/// esse conversor, o System.Text.Json perde as coordenadas e os elementos aparecem
/// como "linhas sem sentido".
/// </summary>
internal sealed class OpcadPrimitiveConverter : JsonConverter<OpcadPrimitive>
{
    public override OpcadPrimitive? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var tipo = (root.TryGetProperty("type", out var t) ? t.GetString() : null)
                       ?? (root.TryGetProperty("$type", out var d) ? d.GetString() : null);

            var json = root.GetRawText();
            return tipo switch
            {
                "line" => JsonSerializer.Deserialize<OpcadLinePrimitive>(json, options),
                "rect" => JsonSerializer.Deserialize<OpcadRectPrimitive>(json, options),
                "ellipse" => JsonSerializer.Deserialize<OpcadEllipsePrimitive>(json, options),
                "circle" => JsonSerializer.Deserialize<OpcadCirclePrimitive>(json, options),
                "arc" => JsonSerializer.Deserialize<OpcadArcPrimitive>(json, options),
                "polygon" => JsonSerializer.Deserialize<OpcadPolygonPrimitive>(json, options),
                "polyline" => JsonSerializer.Deserialize<OpcadPolylinePrimitive>(json, options),
                "text" => JsonSerializer.Deserialize<OpcadTextPrimitive>(json, options),
                // Tipo desconhecido (ex.: "polyline"): retorna null em vez de
                // desserializar o tipo base — isso causava recursão infinita
                // (stack overflow), pois OpcadPrimitive é o tipo deste conversor.
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, OpcadPrimitive value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
