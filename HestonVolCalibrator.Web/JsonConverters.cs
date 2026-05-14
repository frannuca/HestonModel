using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HestonVolCalibrator.Web;

// Emit NaN / +-Inf as JSON null so consumers (browsers) can parse the response.
// Reads: null -> NaN, otherwise the double value.
public sealed class NaNAsNullDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        if (reader.TokenType == JsonTokenType.Null) return double.NaN;
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) w.WriteNullValue();
        else w.WriteNumberValue(v);
    }
}
