using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Models.Misc;

/// <summary>
/// Ein einzelner GPS-Punkt einer Positions-Spur (Trace), der über die Valhalla-Meili-Engine
/// auf das Straßennetz abgeglichen ("map-matched") werden soll.
/// </summary>
public class TracePoint
{
    /// <summary>
    /// Breitengrad des GPS-Punktes (WGS84). Gültiger Bereich: -90 bis 90.
    /// </summary>
    [Range(-90.0, 90.0, ErrorMessage = "Latitude muss zwischen -90 und 90 liegen.")]
    [Description("Breitengrad des GPS-Punktes (WGS84).")]
    public double Latitude { get; set; }

    /// <summary>
    /// Längengrad des GPS-Punktes (WGS84). Gültiger Bereich: -180 bis 180.
    /// </summary>
    [Range(-180.0, 180.0, ErrorMessage = "Longitude muss zwischen -180 und 180 liegen.")]
    [Description("Längengrad des GPS-Punktes (WGS84).")]
    public double Longitude { get; set; }

    /// <summary>
    /// Optionaler Zeitstempel des Punktes in Sekunden (Unix-Zeit oder relativ zum Trace-Start).
    /// Wird dringend empfohlen: Meili kann mit Zeitinformationen Geschwindigkeiten plausibilisieren
    /// und liefert dadurch deutlich präzisere Ergebnisse als bei reinen Positionsdaten.
    /// </summary>
    [Description("Optionaler Zeitstempel in Sekunden. Verbessert die Match-Qualität erheblich.")]
    public double? Time { get; set; }

    /// <summary>
    /// Optionaler Wegpunkt-Typ für die Behandlung durch die Engine:
    /// "break", "via", "through" oder "break_through". Ohne Angabe entscheidet Valhalla automatisch.
    /// </summary>
    [Description("Optionaler Wegpunkt-Typ: break | via | through | break_through.")]
    public string? Type { get; set; }
}

/// <summary>
/// Anfrage zum Live-Map-Matching einer GPS-Spur gegen das Straßennetz der Valhalla-Instanz.
/// </summary>
public class MapMatchRequest
{
    /// <summary>
    /// Die zu matchenden GPS-Punkte in chronologischer Reihenfolge. Mindestens 2 Punkte erforderlich.
    /// </summary>
    [Required]
    [MinLength(2, ErrorMessage = "Es werden mindestens 2 GPS-Punkte benötigt.")]
    [Description("Die GPS-Punkte der Spur in chronologischer Reihenfolge.")]
    public List<TracePoint> Points { get; set; } = new();

    /// <summary>
    /// Kostenmodell (Fortbewegungsart) für das Matching. Standard: "auto".
    /// Zulässig u. a.: auto, auto_shorter, truck, bus, taxi, bicycle, pedestrian, motorcycle, motor_scooter.
    /// </summary>
    [Description("Kostenmodell/Fortbewegungsart, z. B. 'auto', 'truck', 'bicycle', 'pedestrian'.")]
    public string Costing { get; set; } = "auto";

    /// <summary>
    /// Matching-Algorithmus. Standard: "map_snap".
    /// <list type="bullet">
    /// <item><description><c>map_snap</c>: Vollständiges HMM-Map-Matching. Für echte (verrauschte) GPS-Daten empfohlen.</description></item>
    /// <item><description><c>edge_walk</c>: Nur für Spuren, die bereits aus einer Valhalla-Route stammen.</description></item>
    /// <item><description><c>walk_or_snap</c>: Versucht edge_walk und fällt bei Bedarf auf map_snap zurück.</description></item>
    /// </list>
    /// Hinweis: Die pro-Punkt-Zuordnung (<see cref="MapMatchResult.MatchedPoints"/>) wird nur bei
    /// <c>map_snap</c> zuverlässig befüllt – daher der Standardwert.
    /// </summary>
    [Description("Matching-Algorithmus: map_snap | edge_walk | walk_or_snap. Standard: map_snap.")]
    public string ShapeMatch { get; set; } = "map_snap";

    /// <summary>
    /// Suchradius in Metern, in dem pro Messpunkt nach Straßenkandidaten gesucht wird.
    /// Zulässiger Bereich laut Valhalla: 0–100 m. Werte werden serverseitig begrenzt.
    /// </summary>
    [Range(0, 100, ErrorMessage = "SearchRadius muss zwischen 0 und 100 Metern liegen.")]
    [Description("Suchradius in Metern (0–100). Höhere Werte kosten Performance.")]
    public int? SearchRadius { get; set; }

    /// <summary>
    /// Angenommene GPS-Genauigkeit in Metern. Steuert, wie stark Messrauschen erwartet wird.
    /// </summary>
    [Range(0.0, 1000.0, ErrorMessage = "GpsAccuracy muss zwischen 0 und 1000 Metern liegen.")]
    [Description("Angenommene GPS-Genauigkeit in Metern.")]
    public double? GpsAccuracy { get; set; }

    /// <summary>
    /// Straffaktor für Abbiegevorgänge. Höhere Werte (z. B. 500) glätten "hin-und-her"-Sprünge bei
    /// dichten, verrauschten Fußgänger-Traces. Bei bereits guter GPS-Qualität eher niedrig lassen.
    /// </summary>
    [Range(0.0, 100000.0, ErrorMessage = "TurnPenaltyFactor muss zwischen 0 und 100000 liegen.")]
    [Description("Straffaktor für Abbiegevorgänge zur Glättung verrauschter Spuren.")]
    public double? TurnPenaltyFactor { get; set; }

    /// <summary>
    /// Wenn <c>true</c>, werden zusätzlich die Straßensegment-Attribute (Name, Tempo, Straßenklasse …)
    /// zurückgegeben. Standard <c>false</c>, um die Antwortgröße bei Live-Matching klein zu halten.
    /// </summary>
    [Description("Zusätzliche Kantenattribute (Straßensegmente) zurückgeben. Standard: false.")]
    public bool IncludeEdges { get; set; }

    /// <summary>
    /// Wenn <c>true</c>, wird die kodierte Polyline zusätzlich serverseitig in eine Liste von
    /// Koordinaten dekodiert (<see cref="MapMatchResult.Geometry"/>). Standard <c>false</c>
    /// (nur die kompakte Encoded Polyline wird geliefert).
    /// </summary>
    [Description("Encoded Polyline zusätzlich serverseitig zu Koordinaten dekodieren. Standard: false.")]
    public bool DecodeGeometry { get; set; }
}

/// <summary>
/// Eine WGS84-Koordinate (dekodierte Geometrie).
/// </summary>
public class GeoPoint
{
    /// <summary>Breitengrad (WGS84).</summary>
    public double Latitude { get; set; }

    /// <summary>Längengrad (WGS84).</summary>
    public double Longitude { get; set; }
}

/// <summary>
/// Das Ergebnis eines einzelnen auf das Straßennetz abgeglichenen GPS-Punktes.
/// Es besteht eine 1:1-Zuordnung zur Reihenfolge der Eingabepunkte.
/// </summary>
public class MatchedPoint
{
    /// <summary>Breitengrad des auf die Straße "gesnappten" Punktes.</summary>
    public double Latitude { get; set; }

    /// <summary>Längengrad des auf die Straße "gesnappten" Punktes.</summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Match-Typ: "matched" (direkt zugeordnet), "interpolated" (zwischen Kanten interpoliert)
    /// oder "unmatched" (keine Zuordnung möglich).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Index der zugehörigen Kante in <see cref="MapMatchResult.Edges"/>. Null, wenn der Punkt
    /// nicht zugeordnet werden konnte.
    /// </summary>
    public int? EdgeIndex { get; set; }

    /// <summary>
    /// Relative Position entlang der zugeordneten Kante (0.0 = Anfang, 1.0 = Ende).
    /// </summary>
    public double? DistanceAlongEdge { get; set; }

    /// <summary>
    /// Abstand in Metern zwischen dem ursprünglichen GPS-Punkt und dem gematchten Punkt.
    /// Ein guter Indikator für die Match-Qualität pro Punkt.
    /// </summary>
    public double? DistanceFromTracePoint { get; set; }
}

/// <summary>
/// Attribute eines auf das Straßennetz abgeglichenen Kantensegments (optional, siehe
/// <see cref="MapMatchRequest.IncludeEdges"/>).
/// </summary>
public class MatchedEdge
{
    /// <summary>Straßennamen dieses Segments (sofern vorhanden).</summary>
    public List<string>? Names { get; set; }

    /// <summary>OpenStreetMap-Way-ID dieses Segments.</summary>
    public long? WayId { get; set; }

    /// <summary>Länge des Segments in Kilometern.</summary>
    public double? Length { get; set; }

    /// <summary>Fahrgeschwindigkeit laut Routing-Modell (km/h).</summary>
    public double? Speed { get; set; }

    /// <summary>
    /// Zulässige Höchstgeschwindigkeit in km/h, sofern eine Zahl bekannt ist.
    /// </summary>
    /// <remarks>
    /// <c>null</c> bedeutet <b>nicht</b> „unbeschränkt“, sondern „nicht bekannt“. Für
    /// „unbeschränkt“ ist <see cref="SpeedLimitUnlimited"/> maßgeblich.
    /// </remarks>
    public double? SpeedLimit { get; set; }

    /// <summary>
    /// <c>true</c>, wenn für dieses Segment ausdrücklich <em>keine</em> Geschwindigkeitsbeschränkung
    /// gilt (in OpenStreetMap <c>maxspeed=none</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Die Routing-Engine meldet diesen Zustand als Wortmarke und nicht als Zahl. Er wird hier
    /// getrennt ausgewiesen, weil „unbeschränkt“ eine Aussage über die Straße und „unbekannt“ eine
    /// Aussage über die Datenlage ist — beides auf <c>null</c> abzubilden würde die Unterscheidung
    /// vernichten.
    /// </para>
    /// <para>
    /// <b>Neu.</b> Rein additiv; vorhandene Auswertungen von <see cref="SpeedLimit"/> bleiben gültig.
    /// </para>
    /// </remarks>
    public bool SpeedLimitUnlimited { get; set; }

    /// <summary>Straßenklasse (motorway, trunk, primary, …).</summary>
    public string? RoadClass { get; set; }

    /// <summary>Nutzungsart der Kante (road, ramp, driveway, …).</summary>
    public string? Use { get; set; }

    /// <summary>Startindex dieses Segments in der Gesamtgeometrie (Shape).</summary>
    public int? BeginShapeIndex { get; set; }

    /// <summary>Endindex dieses Segments in der Gesamtgeometrie (Shape).</summary>
    public int? EndShapeIndex { get; set; }
}

/// <summary>
/// Ergebnis eines Live-Map-Matchings (Valhalla-Meili <c>trace_attributes</c>).
/// </summary>
public class MapMatchResult
{
    /// <summary>Anzahl der übermittelten Eingabepunkte.</summary>
    public int InputPointCount { get; set; }

    /// <summary>Anzahl der zurückgegebenen gematchten Punkte.</summary>
    public int MatchedPointCount { get; set; }

    /// <summary>
    /// Güte des Abgleichs, wie von der Routing-Engine bewertet (höher ist besser).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vor jeder Auswertung des Ergebnisses prüfen.</b> Meili legt <em>jede</em> Spur auf
    /// Straßen — auch eine mit Lücken, Ausreißern oder zu grober Abtastung. Ohne diese Kennzahl ist
    /// nicht unterscheidbar, ob eine Spur gut abgeglichen wurde oder plausibel aussehend falsch.
    /// </para>
    /// <para>
    /// <c>null</c>, wenn die eingesetzte Valhalla-Fassung den Wert nicht liefert.
    /// </para>
    /// <para><b>Neu.</b> Rein additiv.</para>
    /// </remarks>
    public double? ConfidenceScore { get; set; }

    /// <summary>
    /// Die auf das Straßennetz abgeglichene Gesamtgeometrie als Encoded Polyline (Präzision 1e-6).
    /// </summary>
    public string? Shape { get; set; }

    /// <summary>
    /// Dekodierte Geometrie als Koordinatenliste. Nur befüllt, wenn
    /// <see cref="MapMatchRequest.DecodeGeometry"/> gesetzt ist.
    /// </summary>
    public List<GeoPoint>? Geometry { get; set; }

    /// <summary>Die pro Eingabepunkt gematchten Positionen (1:1-Zuordnung).</summary>
    public List<MatchedPoint> MatchedPoints { get; set; } = new();

    /// <summary>
    /// Attribute der durchlaufenen Straßensegmente. Nur befüllt, wenn
    /// <see cref="MapMatchRequest.IncludeEdges"/> gesetzt ist.
    /// </summary>
    public List<MatchedEdge>? Edges { get; set; }
}

/// <summary>
/// Ergebnis eines Map-Matchings mit vollständiger Routen-Rückgabe (Valhalla-Meili <c>trace_route</c>).
/// </summary>
public class MapRouteResult
{
    /// <summary>Gesamtlänge der gesnappten Route in Kilometern.</summary>
    public double LengthKm { get; set; }

    /// <summary>Geschätzte Gesamtdauer der Route in Sekunden.</summary>
    public double TimeSeconds { get; set; }

    /// <summary>
    /// Die Encoded Polyline (Präzision 1e-6) je Teilabschnitt (Leg) der gesnappten Route.
    /// </summary>
    public List<string> LegShapes { get; set; } = new();

    /// <summary>
    /// Dekodierte Gesamtgeometrie als Koordinatenliste. Nur befüllt, wenn
    /// <see cref="MapMatchRequest.DecodeGeometry"/> gesetzt ist.
    /// </summary>
    public List<GeoPoint>? Geometry { get; set; }
}
