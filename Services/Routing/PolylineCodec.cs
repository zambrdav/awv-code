namespace SmartRoutingAPI.Services.Routing;

/// <summary>Eine WGS84-Koordinate in der internen Routing-Schicht.</summary>
/// <param name="Latitude">Breitengrad in Grad.</param>
/// <param name="Longitude">Längengrad in Grad.</param>
public readonly record struct Coordinate(double Latitude, double Longitude);

/// <summary>
/// Kodierung und Dekodierung von Encoded Polylines sowie das <em>korrekte</em> Zusammenfügen
/// mehrerer Teilgeometrien.
/// </summary>
/// <remarks>
/// <para>
/// Encoded Polylines speichern Differenzen zum jeweils vorhergehenden Punkt. Zwei kodierte
/// Zeichenketten dürfen deshalb <b>niemals</b> als Strings aneinandergehängt werden: Der erste
/// Wert des zweiten Abschnitts ist eine Differenz zum Ursprung <c>(0,0)</c> seines eigenen
/// Bezugssystems und würde vom Dekoder als Sprung ab dem letzten Punkt des ersten Abschnitts
/// interpretiert. Genau dieser Fehler (<c>Geometry += leg.Geometry</c>) machte in der Vorversion
/// jede mehrteilige Route unbrauchbar.
/// </para>
/// <para>
/// Korrekt ist: dekodieren, Koordinaten verketten, einmal neu kodieren – genau das leistet
/// <see cref="Stitch"/>.
/// </para>
/// </remarks>
public static class PolylineCodec
{
    /// <summary>Valhalla liefert Polylines mit sechs Nachkommastellen ("polyline6").</summary>
    public const int ValhallaPrecision = 6;

    /// <summary>Google-Standard mit fünf Nachkommastellen ("polyline5").</summary>
    public const int GooglePrecision = 5;

    /// <summary>
    /// Dekodiert eine Encoded Polyline in ihre Koordinatenfolge.
    /// </summary>
    /// <param name="encoded">Die kodierte Polyline. <c>null</c> oder leer ergibt eine leere Liste.</param>
    /// <param name="precision">Anzahl der Nachkommastellen (Standard: Valhalla, 6).</param>
    /// <returns>Die Koordinaten in ursprünglicher Reihenfolge.</returns>
    /// <exception cref="FormatException">Wenn die Polyline abgeschnitten oder fehlerhaft ist.</exception>
    public static List<Coordinate> Decode(string? encoded, int precision = ValhallaPrecision)
    {
        try
        {
            var coordinates = new List<Coordinate>();
            if (string.IsNullOrEmpty(encoded))
            {
                return coordinates;
            }

            double factor = Math.Pow(10, precision);
            int index = 0;
            int latitude = 0;
            int longitude = 0;

            while (index < encoded.Length)
            {
                latitude += DecodeSignedValue(encoded, ref index);
                longitude += DecodeSignedValue(encoded, ref index);

                coordinates.Add(new Coordinate(latitude / factor, longitude / factor));
            }

            return coordinates;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (IndexOutOfRangeException ex)
        {
            // Abgeschnittene Polyline: der letzte Wert endet mitten in einer Zeichenfolge.
            throw new FormatException("Die Encoded Polyline ist unvollständig oder fehlerhaft.", ex);
        }
        catch (Exception ex)
        {
            throw new FormatException("Die Encoded Polyline konnte nicht dekodiert werden.", ex);
        }
    }

    /// <summary>
    /// Kodiert eine Koordinatenfolge als Encoded Polyline.
    /// </summary>
    /// <param name="coordinates">Die zu kodierenden Koordinaten.</param>
    /// <param name="precision">Anzahl der Nachkommastellen (Standard: Valhalla, 6).</param>
    /// <returns>Die kodierte Polyline; leere Eingabe ergibt eine leere Zeichenkette.</returns>
    public static string Encode(IReadOnlyList<Coordinate>? coordinates, int precision = ValhallaPrecision)
    {
        try
        {
            if (coordinates is null || coordinates.Count == 0)
            {
                return string.Empty;
            }

            double factor = Math.Pow(10, precision);

            // Fünf Zeichen je Wert und zwei Werte je Punkt sind eine sichere Vorabschätzung.
            var builder = new System.Text.StringBuilder(coordinates.Count * 12);
            long previousLatitude = 0;
            long previousLongitude = 0;

            foreach (var coordinate in coordinates)
            {
                long latitude = (long)Math.Round(coordinate.Latitude * factor, MidpointRounding.AwayFromZero);
                long longitude = (long)Math.Round(coordinate.Longitude * factor, MidpointRounding.AwayFromZero);

                AppendSignedValue(builder, latitude - previousLatitude);
                AppendSignedValue(builder, longitude - previousLongitude);

                previousLatitude = latitude;
                previousLongitude = longitude;
            }

            return builder.ToString();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die Koordinatenfolge konnte nicht kodiert werden.", ex);
        }
    }

    /// <summary>
    /// Fügt mehrere kodierte Teilgeometrien zu <em>einer</em> gültigen Polyline zusammen.
    /// </summary>
    /// <param name="encodedSegments">Die Teilgeometrien in Fahrtreihenfolge.</param>
    /// <param name="precision">Anzahl der Nachkommastellen (Standard: Valhalla, 6).</param>
    /// <returns>Eine einzelne, gültige Encoded Polyline.</returns>
    /// <remarks>
    /// Aufeinanderfolgende Abschnitte teilen ihren Übergangspunkt. Dieser wird genau einmal
    /// übernommen, damit keine Nullstrecken-Duplikate in der Geometrie stehen.
    /// </remarks>
    public static string Stitch(IEnumerable<string?> encodedSegments, int precision = ValhallaPrecision)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(encodedSegments);

            var merged = new List<Coordinate>();
            foreach (var segment in encodedSegments)
            {
                var decoded = Decode(segment, precision);
                if (decoded.Count == 0)
                {
                    continue;
                }

                int start = 0;
                if (merged.Count > 0 && IsSamePoint(merged[^1], decoded[0], precision))
                {
                    start = 1;
                }

                for (int i = start; i < decoded.Count; i++)
                {
                    merged.Add(decoded[i]);
                }
            }

            return Encode(merged, precision);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die Teilgeometrien konnten nicht zusammengefügt werden.", ex);
        }
    }

    /// <summary>
    /// Prüft, ob zwei Koordinaten bei der gegebenen Genauigkeit denselben Punkt bezeichnen.
    /// </summary>
    private static bool IsSamePoint(Coordinate left, Coordinate right, int precision)
    {
        // Eine halbe Einheit der letzten kodierbaren Stelle ist die feinste sinnvolle Toleranz.
        double tolerance = 0.5 / Math.Pow(10, precision);
        return Math.Abs(left.Latitude - right.Latitude) <= tolerance
            && Math.Abs(left.Longitude - right.Longitude) <= tolerance;
    }

    /// <summary>
    /// Dekodiert einen ZigZag-kodierten Differenzwert und setzt <paramref name="index"/> hinter das
    /// gelesene Segment.
    /// </summary>
    private static int DecodeSignedValue(string encoded, ref int index)
    {
        int shift = 0;
        int result = 0;
        int current;

        do
        {
            if (index >= encoded.Length)
            {
                throw new FormatException("Die Encoded Polyline endet innerhalb eines Wertes.");
            }

            current = encoded[index++] - 63;    // Basis-64-Offset gemäß Polyline-Spezifikation.
            result |= (current & 0x1f) << shift; // Fünf Nutzbits je Zeichen.
            shift += 5;
        }
        while (current >= 0x20);                 // Fortsetzungsbit gesetzt: ein weiteres Zeichen folgt.

        // ZigZag-Dekodierung: ungerade Werte stehen für negative Zahlen.
        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }

    /// <summary>Schreibt einen Differenzwert ZigZag-kodiert in den Puffer.</summary>
    private static void AppendSignedValue(System.Text.StringBuilder builder, long value)
    {
        // ZigZag-Kodierung: Vorzeichen in das niedrigste Bit verlagern.
        long zigzag = value < 0 ? ~(value << 1) : value << 1;

        while (zigzag >= 0x20)
        {
            builder.Append((char)((0x20 | (int)(zigzag & 0x1f)) + 63));
            zigzag >>= 5;
        }

        builder.Append((char)((int)zigzag + 63));
    }
}
