using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartRoutingAPI.Services.Json;

// =================================================================================================
// Duldsame Leser für die Valhalla-Wire-Modelle.
//
// WARUM DIESE DATEI EXISTIERT
// ---------------------------
// Valhalla ist ein eigenständig versioniertes Fremdsystem. Sein JSON ist an mehreren Stellen
// NICHT typstabil, und genau das hat den Endpunkt /api/MapMatching/match zum Ausfall gebracht:
//
//   src/tyr/trace_serializer.cc
//     if (controller(kEdgeSpeedLimit) && (edge.speed_limit() > 0)) {
//       if (edge.speed_limit() == kUnlimitedSpeedLimit) {
//         writer("speed_limit", "unlimited");          // <-- ZEICHENKETTE
//       } else {
//         writer("speed_limit", serialize_speed(...)); // <-- ZAHL
//       }
//     }
//
// Ein Feld, das je Kante entweder Zahl oder Zeichenkette ist, lässt sich nicht auf ein
// <c>double?</c> binden. Auf einer Autobahn ohne Tempolimit (in Deutschland der Regelfall bei
// <c>maxspeed=none</c>) liefert Valhalla "unlimited", System.Text.Json wirft eine
// <see cref="JsonException"/>, und die gesamte Antwort ist verloren — obwohl das Map-Matching
// selbst erfolgreich war. Der Aufrufer sah nur noch:
//   {"error":"Die Antwort der Valhalla-Instanz konnte nicht verarbeitet werden."}
//
// Gleiches Muster, zweite Fehlerquelle: <c>matched_points[].edge_index</c> wird als
// <c>uint64_t</c> geschrieben. Ältere bzw. abweichend gepatchte Valhalla-Builds geben dort den
// Kennwert <c>kInvalidEdgeIndex</c> (2^32-1 bzw. 2^64-1) aus, statt das Feld weganzulassen. Ein
// solcher Wert passt in kein <c>int?</c> und beendet das Deserialisieren ebenfalls hart.
//
// LEITSATZ
// --------
// Ein unerwarteter Einzelwert eines Fremdsystems darf niemals eine vollständig gültige Antwort
// vernichten. Diese Leser geben in genau diesem Fall <c>null</c> zurück ("Wert unbekannt") statt
// zu werfen. Sie sind ausdrücklich NUR für Valhalla-Antworten gedacht — nicht für den
// Anfragekörper der eigenen API, wo <c>JsonUnmappedMemberHandling.Disallow</c> und strenge
// Validierung bewusst gelten.
// =================================================================================================

/// <summary>
/// Liest ein <see cref="int"/>-Feld einer Valhalla-Antwort duldsam: Zahlen beliebiger Breite,
/// Zahlen in Zeichenketten und alles Übrige werden verlustfrei auf <c>int?</c> abgebildet, wobei
/// nicht darstellbare Werte zu <c>null</c> werden.
/// </summary>
/// <remarks>
/// Der Anwendungsfall ist <c>matched_points[].edge_index</c>: Valhalla serialisiert dort einen
/// <c>uint64_t</c>. Ein Kennwert wie 4294967295 ist kein Index, sondern die Aussage „nicht
/// zugeordnet“ — <c>null</c> ist dafür die richtige Darstellung, ein Ausnahmefehler nicht.
/// </remarks>
public sealed class TolerantNullableInt32Converter : JsonConverter<int?>
{
    /// <summary>
    /// Muss <c>true</c> sein, damit dieser Leser auch das <c>null</c>-Token selbst behandelt und
    /// nicht von System.Text.Json übersprungen wird.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.Number:
                    if (reader.TryGetInt32(out int exact))
                    {
                        return exact;
                    }

                    // Breiter als Int32 (Valhalla schreibt uint64) oder mit Nachkommaanteil: Der
                    // Wert ist entweder ein Kennwert oder für einen Index unbrauchbar.
                    if (reader.TryGetDouble(out double wide)
                        && wide >= int.MinValue
                        && wide <= int.MaxValue)
                    {
                        return (int)wide;
                    }

                    return null;

                case JsonTokenType.String:
                    return int.TryParse(
                        reader.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsed)
                        ? parsed
                        : null;

                default:
                    // Strukturell unerwartet (Objekt/Feld/Wahrheitswert): Token vollständig
                    // überspringen, damit der Leser danach wieder an einer gültigen Stelle steht.
                    reader.Skip();
                    return null;
            }
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            // Ein duldsamer Leser darf ausschließlich an einem echten Strukturfehler scheitern.
            return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Liest ein <see cref="long"/>-Feld einer Valhalla-Antwort duldsam. Anwendungsfall:
/// <c>edge.way_id</c> ist ein <c>uint64</c> und überschreitet damit den Wertebereich von
/// <c>long</c> theoretisch.
/// </summary>
public sealed class TolerantNullableInt64Converter : JsonConverter<long?>
{
    /// <inheritdoc cref="TolerantNullableInt32Converter.HandleNull" />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long exact))
                    {
                        return exact;
                    }

                    // Ein OSM-Way jenseits von 2^63 existiert nicht; ein solcher Wert ist ein
                    // Kennwert und trägt keine fachliche Information.
                    return null;

                case JsonTokenType.String:
                    return long.TryParse(
                        reader.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long parsed)
                        ? parsed
                        : null;

                default:
                    reader.Skip();
                    return null;
            }
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Liest ein <see cref="double"/>-Feld einer Valhalla-Antwort duldsam: Zahl, Zahl als Zeichenkette
/// oder — bei allem Übrigen — <c>null</c>.
/// </summary>
/// <remarks>
/// Notwendig, weil Valhalla Zahlenfelder bei Sonderfällen durch Wortmarken ersetzt (siehe
/// <c>speed_limit</c>) und bei Wiederberechnungen (<c>recostings</c>) auch <c>null</c> schreibt.
/// </remarks>
public sealed class TolerantNullableDoubleConverter : JsonConverter<double?>
{
    /// <inheritdoc cref="TolerantNullableInt32Converter.HandleNull" />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.Number:
                    return reader.TryGetDouble(out double value) ? value : null;

                case JsonTokenType.String:
                    return double.TryParse(
                        reader.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsed)
                        ? parsed
                        : null;

                default:
                    reader.Skip();
                    return null;
            }
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Die zulässige Höchstgeschwindigkeit einer Straßenkante, wie Valhalla sie meldet.
/// </summary>
/// <remarks>
/// <para>
/// Valhalla unterscheidet drei Zustände, die zuvor alle auf <c>double?</c> abgebildet wurden und
/// dadurch nicht unterscheidbar waren:
/// </para>
/// <list type="bullet">
///   <item><description><b>bekannt</b> — eine Zahl in km/h.</description></item>
///   <item><description><b>unbeschränkt</b> — die Zeichenkette <c>"unlimited"</c>
///   (<c>maxspeed=none</c>, in Deutschland auf Autobahnen der Regelfall).</description></item>
///   <item><description><b>unbekannt</b> — das Feld fehlt.</description></item>
/// </list>
/// <para>
/// „Unbeschränkt“ und „unbekannt“ sind fachlich <em>nicht</em> dasselbe: Das erste ist eine
/// Aussage über die Straße, das zweite über die Datenlage. Ein Fahrerhinweis darf sie nicht
/// verwechseln.
/// </para>
/// </remarks>
/// <param name="Kph">Die Höchstgeschwindigkeit in km/h, sofern eine Zahl gemeldet wurde.</param>
/// <param name="IsUnlimited">
/// <c>true</c>, wenn Valhalla ausdrücklich „keine Beschränkung“ gemeldet hat.
/// </param>
public readonly record struct ValhallaSpeedLimit(double? Kph, bool IsUnlimited)
{
    /// <summary>Es liegt keine Angabe vor.</summary>
    public static readonly ValhallaSpeedLimit Unknown = new(null, false);

    /// <summary>Die Kante ist ausdrücklich ohne Geschwindigkeitsbeschränkung.</summary>
    public static readonly ValhallaSpeedLimit Unlimited = new(null, true);
}

/// <summary>
/// Liest Valhallas polymorphes <c>edge.speed_limit</c> (Zahl <em>oder</em> die Zeichenkette
/// <c>"unlimited"</c>) in ein <see cref="ValhallaSpeedLimit"/>.
/// </summary>
/// <remarks>
/// Dies ist der unmittelbare Fehlerbeheber für <c>POST /api/MapMatching/match</c> mit
/// <c>includeEdges: true</c>.
/// </remarks>
public sealed class ValhallaSpeedLimitConverter : JsonConverter<ValhallaSpeedLimit>
{
    /// <summary>Die von Valhalla verwendete Wortmarke für „keine Beschränkung“.</summary>
    private const string UnlimitedToken = "unlimited";

    /// <inheritdoc cref="TolerantNullableInt32Converter.HandleNull" />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override ValhallaSpeedLimit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return ValhallaSpeedLimit.Unknown;

                case JsonTokenType.Number:
                    return reader.TryGetDouble(out double kph)
                        ? new ValhallaSpeedLimit(kph, false)
                        : ValhallaSpeedLimit.Unknown;

                case JsonTokenType.String:
                    string? text = reader.GetString();

                    if (string.Equals(text, UnlimitedToken, StringComparison.OrdinalIgnoreCase))
                    {
                        return ValhallaSpeedLimit.Unlimited;
                    }

                    return double.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsed)
                        ? new ValhallaSpeedLimit(parsed, false)
                        : ValhallaSpeedLimit.Unknown;

                default:
                    reader.Skip();
                    return ValhallaSpeedLimit.Unknown;
            }
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            return ValhallaSpeedLimit.Unknown;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ValhallaSpeedLimit value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.IsUnlimited)
        {
            writer.WriteStringValue(UnlimitedToken);
            return;
        }

        if (value.Kph.HasValue)
        {
            writer.WriteNumberValue(value.Kph.Value);
            return;
        }

        writer.WriteNullValue();
    }
}
