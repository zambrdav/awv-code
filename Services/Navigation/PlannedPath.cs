using SmartRoutingAPI.Services.Routing;

namespace SmartRoutingAPI.Services.Navigation;

/// <summary>
/// Das Ergebnis einer Lotfußpunkt-Projektion einer Position auf eine geplante Strecke.
/// </summary>
/// <param name="SegmentIndex">
/// Index des Streckenabschnitts, auf den projiziert wurde. Entspricht dem Index des
/// <em>Anfangspunkts</em> in der Geometrie und ist damit unmittelbar als Shape-Index verwendbar.
/// </param>
/// <param name="FractionAlongSegment">Relative Lage im Abschnitt: 0 = Anfang, 1 = Ende.</param>
/// <param name="SnappedLatitude">Breitengrad des Lotfußpunkts auf der Strecke.</param>
/// <param name="SnappedLongitude">Längengrad des Lotfußpunkts auf der Strecke.</param>
/// <param name="LateralOffsetMeters">
/// Querabstand in Metern zwischen der gemeldeten Position und der Strecke. <b>Dies ist die
/// eigentliche Messgröße der Abweichungserkennung.</b>
/// </param>
/// <param name="DistanceAlongPathMeters">Zurückgelegte Streckenlänge bis zum Lotfußpunkt in Metern.</param>
public readonly record struct PathProjection(
    int SegmentIndex,
    double FractionAlongSegment,
    double SnappedLatitude,
    double SnappedLongitude,
    double LateralOffsetMeters,
    double DistanceAlongPathMeters);

/// <summary>
/// Eine geplante Strecke in einer für wiederholtes Projizieren vorbereiteten Form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warum nicht Valhalla-Meili für die Live-Position?</b> Meili (<c>trace_attributes</c>) ist ein
/// Hidden-Markov-Abgleich gegen das <em>gesamte</em> Straßennetz. Es beantwortet die Frage „auf
/// welcher Straße fährt dieses Fahrzeug?“ — nicht die Frage „ist dieses Fahrzeug noch auf Route
/// X?“. Für die zweite Frage ist der Lotfußpunkt auf die bereits berechnete Streckengeometrie die
/// exakte und vollständige Antwort: kein Netzaufruf, keine Modellunsicherheit, kein
/// Rechenzeitbudget. Meili bleibt das richtige Werkzeug für die nachträgliche Abgleichung
/// aufgezeichneter Spuren (<c>/api/MapMatching</c>) und ist es für die Live-Position nie gewesen.
/// </para>
/// <para>
/// <b>Verwendetes Erdmodell.</b> Je Abschnitt wird eine lokale, längentreue Ebene aufgespannt: Die
/// Meter je Breiten- und Längengrad werden aus den WGS84-Reihenentwicklungen an der
/// <em>Mittelbreite des jeweiligen Abschnitts</em> berechnet. Bei Abschnittslängen unterhalb eines
/// Kilometers — bei polyline6-Geometrien liegen sie typischerweise unter 30 m — ist der Fehler
/// kleiner als ein Zentimeter. Eine <em>einzelne</em> Bezugsbreite für die ganze Route wäre
/// unzulässig: Über 50 km Nord-Süd-Ausdehnung schwankt <c>cos φ</c> um etwa 0,9 %, was sich in der
/// Längensumme zu mehreren hundert Metern aufaddiert.
/// </para>
/// <para>
/// <b>Rechenaufwand.</b> Die zwei trigonometrischen Funktionen je Abschnitt fallen genau
/// <em>einmal</em> beim Aufbau an. Jede Projektion ist danach reine Arithmetik: zwei
/// Multiplikationen und ein Skalarprodukt je Abschnitt, ohne Wurzel und ohne
/// trigonometrische Funktion im inneren Durchlauf. Damit kostet die Projektion einer Position auf
/// eine Geometrie mit 10 000 Stützpunkten einige Dutzend Mikrosekunden.
/// </para>
/// </remarks>
public sealed class PlannedPath
{
    /// <summary>
    /// Vorberechnete Kenngrößen eines Streckenabschnitts.
    /// </summary>
    /// <remarks>
    /// Als <c>readonly struct</c> in einem Array abgelegt: zusammenhängender Speicher, keine
    /// Referenzverfolgung und kein Druck auf den Garbage Collector im heißen Durchlauf.
    /// </remarks>
    private readonly struct Segment
    {
        /// <summary>Meter je Breitengrad an der Mittelbreite dieses Abschnitts.</summary>
        public readonly double MetersPerDegreeLatitude;

        /// <summary>Meter je Längengrad an der Mittelbreite dieses Abschnitts.</summary>
        public readonly double MetersPerDegreeLongitude;

        /// <summary>Ostkomponente des Abschnittsvektors in Metern.</summary>
        public readonly double DeltaEastMeters;

        /// <summary>Nordkomponente des Abschnittsvektors in Metern.</summary>
        public readonly double DeltaNorthMeters;

        /// <summary>Quadrat der Abschnittslänge — erspart im Durchlauf eine Wurzel.</summary>
        public readonly double SquaredLengthMeters;

        /// <summary>Abschnittslänge in Metern.</summary>
        public readonly double LengthMeters;

        /// <summary>Zurückgelegte Streckenlänge bis zum Anfang dieses Abschnitts.</summary>
        public readonly double CumulativeStartMeters;

        /// <summary>Erzeugt die Kenngrößen eines Abschnitts.</summary>
        public Segment(
            double metersPerDegreeLatitude,
            double metersPerDegreeLongitude,
            double deltaEastMeters,
            double deltaNorthMeters,
            double cumulativeStartMeters)
        {
            MetersPerDegreeLatitude = metersPerDegreeLatitude;
            MetersPerDegreeLongitude = metersPerDegreeLongitude;
            DeltaEastMeters = deltaEastMeters;
            DeltaNorthMeters = deltaNorthMeters;
            SquaredLengthMeters = (deltaEastMeters * deltaEastMeters) + (deltaNorthMeters * deltaNorthMeters);
            LengthMeters = Math.Sqrt(SquaredLengthMeters);
            CumulativeStartMeters = cumulativeStartMeters;
        }
    }

    private readonly Coordinate[] _points;
    private readonly Segment[] _segments;

    /// <summary>Die Stützpunkte der Strecke in Fahrtreihenfolge.</summary>
    public IReadOnlyList<Coordinate> Points => _points;

    /// <summary>Gesamtlänge der Strecke in Metern.</summary>
    public double TotalLengthMeters { get; }

    /// <summary>
    /// Ob die Strecke projizierbar ist. Erfordert mindestens zwei Stützpunkte <em>und</em> eine
    /// Länge größer null.
    /// </summary>
    /// <remarks>
    /// Eine Geometrie aus einem einzelnen oder aus ausschließlich deckungsgleichen Punkten ist
    /// keine Strecke. Der Aufrufer muss diesen Fall als fachlichen Fehler behandeln und darf ihn
    /// nicht in eine Projektion mit Fantasiewerten übersetzen.
    /// </remarks>
    public bool IsProjectable => _segments.Length > 0 && TotalLengthMeters > 0.0;

    /// <summary>Erzeugt eine Strecke aus ihren Stützpunkten.</summary>
    /// <param name="points">Die Stützpunkte in Fahrtreihenfolge.</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> ist <c>null</c>.</exception>
    private PlannedPath(IReadOnlyList<Coordinate> points)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(points);

            _points = points as Coordinate[] ?? points.ToArray();

            int segmentCount = Math.Max(0, _points.Length - 1);
            _segments = new Segment[segmentCount];

            double cumulative = 0.0;

            for (int i = 0; i < segmentCount; i++)
            {
                var from = _points[i];
                var to = _points[i + 1];

                double midLatitudeDegrees = (from.Latitude + to.Latitude) * 0.5;
                (double metersPerLatitude, double metersPerLongitude) = MetersPerDegree(midLatitudeDegrees);

                double deltaEast = (to.Longitude - from.Longitude) * metersPerLongitude;
                double deltaNorth = (to.Latitude - from.Latitude) * metersPerLatitude;

                var segment = new Segment(
                    metersPerLatitude,
                    metersPerLongitude,
                    deltaEast,
                    deltaNorth,
                    cumulative);

                _segments[i] = segment;
                cumulative += segment.LengthMeters;
            }

            TotalLengthMeters = cumulative;
        }
        catch (ArgumentNullException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Die geplante Strecke konnte nicht für die Projektion vorbereitet werden.", ex);
        }
    }

    /// <summary>Erzeugt eine Strecke aus einer Liste von Koordinaten.</summary>
    /// <param name="points">Die Stützpunkte in Fahrtreihenfolge.</param>
    /// <returns>Die vorbereitete Strecke.</returns>
    public static PlannedPath FromPoints(IReadOnlyList<Coordinate> points) => new(points);

    /// <summary>
    /// Erzeugt eine Strecke aus einer Encoded Polyline in Valhalla-Genauigkeit (1e-6).
    /// </summary>
    /// <param name="encodedPolyline">Die kodierte Geometrie; <c>null</c> oder leer ergibt eine leere Strecke.</param>
    /// <returns>Die vorbereitete Strecke.</returns>
    /// <exception cref="FormatException">Die Polyline ist fehlerhaft oder abgeschnitten.</exception>
    public static PlannedPath FromEncodedPolyline(string? encodedPolyline)
        => new(PolylineCodec.Decode(encodedPolyline));

    /// <summary>
    /// Projiziert eine Position senkrecht auf die Strecke und liefert den nächstgelegenen Punkt.
    /// </summary>
    /// <param name="latitude">Breitengrad der Position.</param>
    /// <param name="longitude">Längengrad der Position.</param>
    /// <param name="fromSegmentIndex">
    /// Abschnitt, ab dem gesucht wird. Ermöglicht das <em>monotone</em> Projizieren einer Folge von
    /// Punkten, deren Reihenfolge entlang der Strecke bekannt ist.
    /// </param>
    /// <returns>Die Projektion mit Querabstand und zurückgelegter Streckenlänge.</returns>
    /// <remarks>
    /// <para>
    /// <b>Wozu <paramref name="fromSegmentIndex"/>.</b> Eine Sammeltour befährt dieselbe Straße
    /// mehrfach. Ohne Einschränkung des Suchbereichs würde der Lotfußpunkt eines späteren Halts auf
    /// eine <em>frühere</em> Befahrung derselben Straße fallen, und die abgeleiteten Restdistanzen
    /// wären negativ. Da die Halte in Fahrtreihenfolge vorliegen, ist die monotone Suche nicht nur
    /// schneller, sondern die einzige richtige.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Die Strecke ist nicht projizierbar.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromSegmentIndex"/> liegt außerhalb.</exception>
    public PathProjection Project(double latitude, double longitude, int fromSegmentIndex = 0)
    {
        try
        {
            if (!IsProjectable)
            {
                throw new InvalidOperationException(
                    "Die geplante Strecke enthält keine projizierbare Geometrie.");
            }

            if (fromSegmentIndex < 0 || fromSegmentIndex >= _segments.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fromSegmentIndex),
                    fromSegmentIndex,
                    "Der Startabschnitt liegt außerhalb der Geometrie.");
            }

            double bestSquaredOffset = double.MaxValue;
            int bestSegment = fromSegmentIndex;
            double bestFraction = 0.0;

            for (int i = fromSegmentIndex; i < _segments.Length; i++)
            {
                ref readonly Segment segment = ref _segments[i];
                var origin = _points[i];

                // Position im lokalen, längentreuen Bezugssystem des Abschnitts.
                double pointEast = (longitude - origin.Longitude) * segment.MetersPerDegreeLongitude;
                double pointNorth = (latitude - origin.Latitude) * segment.MetersPerDegreeLatitude;

                double fraction;
                if (segment.SquaredLengthMeters <= 0.0)
                {
                    // Deckungsgleiche Stützpunkte kommen in echten Geometrien vor (Kreuzungen,
                    // zusammengefügte Abschnitte). Sie sind kein Fehler, tragen aber keine Richtung.
                    fraction = 0.0;
                }
                else
                {
                    double dot = (pointEast * segment.DeltaEastMeters) + (pointNorth * segment.DeltaNorthMeters);
                    fraction = Math.Clamp(dot / segment.SquaredLengthMeters, 0.0, 1.0);
                }

                double offsetEast = pointEast - (fraction * segment.DeltaEastMeters);
                double offsetNorth = pointNorth - (fraction * segment.DeltaNorthMeters);
                double squaredOffset = (offsetEast * offsetEast) + (offsetNorth * offsetNorth);

                if (squaredOffset < bestSquaredOffset)
                {
                    bestSquaredOffset = squaredOffset;
                    bestSegment = i;
                    bestFraction = fraction;
                }
            }

            ref readonly Segment chosen = ref _segments[bestSegment];
            var start = _points[bestSegment];
            var end = _points[bestSegment + 1];

            return new PathProjection(
                bestSegment,
                bestFraction,
                start.Latitude + ((end.Latitude - start.Latitude) * bestFraction),
                start.Longitude + ((end.Longitude - start.Longitude) * bestFraction),
                Math.Sqrt(bestSquaredOffset),
                chosen.CumulativeStartMeters + (chosen.LengthMeters * bestFraction));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Die Position konnte nicht auf die geplante Strecke projiziert werden.", ex);
        }
    }

    /// <summary>
    /// Ermittelt die Luftlinienentfernung zwischen zwei Koordinaten in Metern.
    /// </summary>
    /// <param name="fromLatitude">Breitengrad des ersten Punktes.</param>
    /// <param name="fromLongitude">Längengrad des ersten Punktes.</param>
    /// <param name="toLatitude">Breitengrad des zweiten Punktes.</param>
    /// <param name="toLongitude">Längengrad des zweiten Punktes.</param>
    /// <returns>Die Entfernung in Metern.</returns>
    /// <remarks>
    /// Dieselbe lokale, längentreue Ebene wie bei der Projektion, aufgespannt an der Mittelbreite
    /// der beiden Punkte. Für die hier auftretenden Entfernungen — Fahrzeug zu Halt, wenige
    /// hundert Meter — ist das genauer als eine Kugelnäherung und benötigt zwei statt fünf
    /// trigonometrischer Funktionen.
    /// </remarks>
    public static double StraightLineDistanceMeters(
        double fromLatitude,
        double fromLongitude,
        double toLatitude,
        double toLongitude)
    {
        (double metersPerLatitude, double metersPerLongitude) =
            MetersPerDegree((fromLatitude + toLatitude) * 0.5);

        double east = (toLongitude - fromLongitude) * metersPerLongitude;
        double north = (toLatitude - fromLatitude) * metersPerLatitude;

        return Math.Sqrt((east * east) + (north * north));
    }

    /// <summary>
    /// Meter je Breiten- und Längengrad auf dem WGS84-Ellipsoid an der angegebenen Breite.
    /// </summary>
    /// <param name="latitudeDegrees">Die Bezugsbreite in Grad.</param>
    /// <returns>Meter je Breitengrad und Meter je Längengrad.</returns>
    /// <remarks>
    /// Die üblichen Reihenentwicklungen für das WGS84-Ellipsoid, genau auf wenige Millimeter. Die
    /// Vielfachen des Winkels werden über die Mehrfachwinkelformeln aus <c>cos φ</c> und
    /// <c>sin φ</c> gebildet, damit nur zwei trigonometrische Funktionen je Aufruf anfallen statt
    /// sechs.
    /// </remarks>
    private static (double MetersPerLatitude, double MetersPerLongitude) MetersPerDegree(double latitudeDegrees)
    {
        double phi = latitudeDegrees * (Math.PI / 180.0);

        double cos1 = Math.Cos(phi);
        double sin1 = Math.Sin(phi);

        double cos2 = 1.0 - (2.0 * sin1 * sin1);                        // cos 2φ
        double cos3 = (4.0 * cos1 * cos1 * cos1) - (3.0 * cos1);        // cos 3φ
        double cos4 = (2.0 * cos2 * cos2) - 1.0;                        // cos 4φ
        double cos5 = (2.0 * cos2 * cos3) - cos1;                       // cos 5φ
        double cos6 = (2.0 * cos3 * cos3) - 1.0;                        // cos 6φ

        double metersPerLatitude =
            111132.92
            - (559.82 * cos2)
            + (1.175 * cos4)
            - (0.0023 * cos6);

        double metersPerLongitude =
            (111412.84 * cos1)
            - (93.5 * cos3)
            + (0.118 * cos5);

        return (metersPerLatitude, metersPerLongitude);
    }
}
