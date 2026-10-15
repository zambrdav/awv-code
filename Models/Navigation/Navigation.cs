using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SmartRoutingAPI.Models.Misc;
using SmartRoutingAPI.Models.Routes;

namespace SmartRoutingAPI.Models.Navigation;

// =================================================================================================
// Verträge der Fahrer-Navigation.
//
// Die API bleibt in dieser Funktion ZUSTANDSLOS. Sie führt kein Fahrzeug, keine Sitzung und keinen
// Fortschritt mit. Jede Anfrage enthält die gemeldete Position, jede Antwort ist eine Messung samt
// Bewertung. Der Zustandsautomat — "welchen Halt fahre ich an", "wie viele abweichende Messpunkte
// habe ich gesehen" — gehört in den Fahrer-Client.
//
// Das ist keine Bequemlichkeit, sondern eine Notwendigkeit: Der Auftragsstatus der Optimierung
// liegt bereits im Arbeitsspeicher des Prozesses und ist damit bei mehreren Instanzen nicht
// tragfähig. Diesen Fehler bei einer sicherheitsrelevanten Fahrerfunktion zu wiederholen, wäre
// nicht vertretbar.
// =================================================================================================

/// <summary>
/// Eine gemeldete Fahrzeugposition.
/// </summary>
public class NavigationPosition
{
    /// <summary>Breitengrad (WGS84).</summary>
    [Range(-90.0, 90.0, ErrorMessage = "Latitude muss zwischen -90 und 90 liegen.")]
    [Description("Breitengrad der Fahrzeugposition (WGS84).")]
    public double Latitude { get; set; }

    /// <summary>Längengrad (WGS84).</summary>
    [Range(-180.0, 180.0, ErrorMessage = "Longitude muss zwischen -180 und 180 liegen.")]
    [Description("Längengrad der Fahrzeugposition (WGS84).")]
    public double Longitude { get; set; }

    /// <summary>
    /// Fahrtrichtung in Grad, von Norden im Uhrzeigersinn (0 = Nord, 90 = Ost).
    /// </summary>
    /// <remarks>
    /// <b>Dringend mitliefern.</b> Ohne Fahrtrichtung ordnet die Routing-Engine das Fahrzeug bei
    /// einer Neuberechnung ebenso wahrscheinlich der Gegenrichtung derselben Straße zu und weist
    /// als erste Anweisung eine Wende an. Der Wert stammt aus <c>GeolocationCoordinates.heading</c>
    /// und ist erst ab einer Mindestgeschwindigkeit belastbar — bei Stillstand liefert der Browser
    /// <c>null</c>, dann bitte auch <c>null</c> senden statt 0.
    /// </remarks>
    [Range(0.0, 360.0, ErrorMessage = "Heading muss zwischen 0 und 360 Grad liegen.")]
    [Description("Fahrtrichtung in Grad von Nord im Uhrzeigersinn. Verbessert die Neuberechnung erheblich.")]
    public double? Heading { get; set; }

    /// <summary>Gemeldete Ortungsgenauigkeit in Metern (aus <c>GeolocationCoordinates.accuracy</c>).</summary>
    /// <remarks>
    /// Wird nicht zum Rechnen verwendet, sondern in der Antwort gespiegelt: Ein Querabstand von
    /// 40 m bei einer Ortungsgenauigkeit von 60 m ist keine Abweichung, sondern Rauschen. Der
    /// Client kann seine Entscheidungsregel damit gewichten.
    /// </remarks>
    [Range(0.0, 100000.0)]
    [Description("Gemeldete Ortungsgenauigkeit in Metern. Wird zur Bewertung der Messung gespiegelt.")]
    public double? AccuracyMeters { get; set; }
}

/// <summary>
/// Eine zu meidende Fläche, angegeben als äußerer Ring.
/// </summary>
/// <remarks>
/// Für eine flächige Sperrung (Baustelle, Veranstaltung, Hochwasser). Für „diese Straße ist hier
/// gesperrt“ ist <see cref="NavigationRequestBase.ExcludeLocations"/> das richtige und deutlich
/// einfachere Mittel.
/// </remarks>
public class ExclusionZone
{
    /// <summary>
    /// Die Eckpunkte des Rings in Umlaufreihenfolge. Der Ring wird serverseitig geschlossen.
    /// </summary>
    [Required]
    [MinLength(3, ErrorMessage = "Eine Sperrfläche benötigt mindestens 3 Eckpunkte.")]
    [MaxLength(1000, ErrorMessage = "Eine Sperrfläche darf höchstens 1000 Eckpunkte haben.")]
    [Description("Die Eckpunkte des äußeren Rings. Wird serverseitig geschlossen.")]
    public List<GeoPoint> Ring { get; set; } = new();
}

/// <summary>
/// Gemeinsame Angaben aller Navigationsanfragen.
/// </summary>
public abstract class NavigationRequestBase
{
    /// <summary>Die aktuelle Fahrzeugposition.</summary>
    [Required]
    [Description("Die aktuelle Fahrzeugposition.")]
    public NavigationPosition Position { get; set; } = new();

    /// <summary>
    /// Punkte, die gemieden werden sollen — der Weg für „diese Straße ist gesperrt“.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jeder Punkt wird auf die nächstgelegene Straße abgebildet, und diese Straße wird aus der
    /// Berechnung ausgeschlossen. Tippt der Fahrer „gesperrt“, sendet der Client die dortige
    /// Koordinate und muss sie bei allen folgenden Neuberechnungen dieser Schicht <b>weiterhin
    /// mitsenden</b>.
    /// </para>
    /// <para>
    /// <b>Ohne diese Angabe ist eine Sperrung unbehandelbar:</b> Die Engine kennt die Sperrung
    /// nicht und führt den Fahrer bei jeder Neuberechnung erneut in die Absperrung hinein.
    /// </para>
    /// </remarks>
    [MaxLength(50, ErrorMessage = "Es können höchstens 50 Sperrpunkte übergeben werden.")]
    [Description("Zu meidende Punkte (gesperrte Straßen). Bei jeder Neuberechnung erneut mitsenden.")]
    public List<GeoPoint> ExcludeLocations { get; set; } = new();

    /// <summary>Zu meidende Flächen.</summary>
    [MaxLength(20, ErrorMessage = "Es können höchstens 20 Sperrflächen übergeben werden.")]
    [Description("Zu meidende Flächen (Baustellen, Veranstaltungen).")]
    public List<ExclusionZone> ExcludePolygons { get; set; } = new();

    /// <summary>
    /// Sprache der Anweisungstexte als BCP-47-Kennzeichen. Ohne Angabe gilt die Konfiguration
    /// (Vorgabe <c>de-DE</c>).
    /// </summary>
    [MaxLength(35)]
    [Description("Sprache der Anweisungstexte, z. B. 'de-DE'. Ohne Angabe gilt die Serverkonfiguration.")]
    public string? Language { get; set; }

    /// <summary>
    /// Wenn <c>true</c>, wird die Geometrie zusätzlich serverseitig in Koordinaten dekodiert.
    /// </summary>
    /// <remarks>
    /// Vorgabe <c>false</c>. Die kodierte Polyline ist um etwa den Faktor zehn kleiner und von
    /// MapLibre-Clients ohnehin zu dekodieren; eine dekodierte Fassung ist bei mobiler Anbindung
    /// eine unnötige Übertragungslast.
    /// </remarks>
    [Description("Geometrie zusätzlich serverseitig dekodieren. Standard: false.")]
    public bool DecodeGeometry { get; set; }
}

/// <summary>
/// Anfrage für Abbiegehinweise zwischen freien Koordinaten.
/// </summary>
public class DirectionsRequest : NavigationRequestBase
{
    /// <summary>Das Ziel.</summary>
    [Required]
    [Description("Das Ziel der Fahrt.")]
    public GeoPoint Destination { get; set; } = new();

    /// <summary>Zwischenziele in Fahrtreihenfolge.</summary>
    [MaxLength(48, ErrorMessage = "Es können höchstens 48 Zwischenziele übergeben werden.")]
    [Description("Zwischenziele in Fahrtreihenfolge.")]
    public List<GeoPoint> Waypoints { get; set; } = new();
}

/// <summary>
/// Anfrage für Abbiegehinweise zum nächsten Halt einer <em>gespeicherten</em> Route.
/// </summary>
/// <remarks>
/// Dies ist der Endpunkt, den ein Fahrer-Client im Regelfall verwendet: Er nennt seine Position und
/// — sofern bekannt — den angefahrenen Halt. Die Zielkoordinate stammt damit aus derselben Quelle,
/// mit der geplant wurde, und kann nicht abweichen.
/// </remarks>
public class RouteGuidanceRequest : NavigationRequestBase
{
    /// <summary>
    /// Die Position des angefahrenen Halts in der Fahrtreihenfolge (<c>step.sequence</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ohne Angabe wird der nächste Halt aus der aktuellen Position abgeleitet. <b>Das ist nur
    /// verlässlich, solange das Fahrzeug auf der Route ist.</b> Fährt es zur Pause oder um eine
    /// Sperrung herum, kann die Ableitung nicht wissen, welcher Halt gemeint ist — der Client führt
    /// diesen Zustand und soll ihn ausdrücklich mitsenden.
    /// </para>
    /// </remarks>
    [Range(0, int.MaxValue)]
    [Description("Position des angefahrenen Halts (step.sequence). Ohne Angabe wird sie abgeleitet.")]
    public int? TargetStopSequence { get; set; }
}

/// <summary>
/// Anfrage für eine Fortschrittsmessung gegen eine gespeicherte Route.
/// </summary>
/// <remarks>
/// Rein rechnerisch: kein Aufruf der Routing-Engine, kein Schreibzugriff.
/// </remarks>
public class RouteProgressRequest
{
    /// <summary>Die aktuelle Fahrzeugposition.</summary>
    [Required]
    [Description("Die aktuelle Fahrzeugposition.")]
    public NavigationPosition Position { get; set; } = new();

    /// <inheritdoc cref="RouteGuidanceRequest.TargetStopSequence" />
    [Range(0, int.MaxValue)]
    [Description("Position des angefahrenen Halts (step.sequence). Ohne Angabe wird sie abgeleitet.")]
    public int? TargetStopSequence { get; set; }
}

/// <summary>
/// Die Art eines Fahrmanövers.
/// </summary>
/// <remarks>
/// <para>
/// Valhalla liefert die Manöverart ausschließlich als Ganzzahl. Nur diese Zahl auszuliefern würde
/// jeden Client zwingen, eine eigene Übersetzungstabelle von 44 Zahlenwerten zu pflegen — und die
/// erste falsche Zeile darin zeigt dem Fahrer ein Linksabbiegen-Symbol an einer Rechtskurve. Die
/// Übersetzung gehört daher einmalig hierher.
/// </para>
/// <para>
/// Die Rohzahl bleibt in <see cref="NavigationManeuver.RawType"/> erhalten: Erweitert Valhalla die
/// Aufzählung, liefert diese API <see cref="Unknown"/> und die unverfälschte Zahl, statt einen
/// falschen Wert zu behaupten.
/// </para>
/// </remarks>
public enum ManeuverType
{
    /// <summary>Von dieser API nicht bekannte Manöverart. Die Rohzahl ist maßgeblich.</summary>
    Unknown = -1,

    /// <summary>Kein Manöver.</summary>
    None = 0,

    /// <summary>Fahrtbeginn.</summary>
    Start = 1,

    /// <summary>Fahrtbeginn nach rechts.</summary>
    StartRight = 2,

    /// <summary>Fahrtbeginn nach links.</summary>
    StartLeft = 3,

    /// <summary>Ziel erreicht.</summary>
    Destination = 4,

    /// <summary>Ziel rechts erreicht.</summary>
    DestinationRight = 5,

    /// <summary>Ziel links erreicht.</summary>
    DestinationLeft = 6,

    /// <summary>Die Straße geht in eine andere über.</summary>
    Becomes = 7,

    /// <summary>Geradeaus weiterfahren.</summary>
    Continue = 8,

    /// <summary>Leicht rechts.</summary>
    SlightRight = 9,

    /// <summary>Rechts abbiegen.</summary>
    Right = 10,

    /// <summary>Scharf rechts.</summary>
    SharpRight = 11,

    /// <summary>Wenden nach rechts.</summary>
    UturnRight = 12,

    /// <summary>Wenden nach links.</summary>
    UturnLeft = 13,

    /// <summary>Scharf links.</summary>
    SharpLeft = 14,

    /// <summary>Links abbiegen.</summary>
    Left = 15,

    /// <summary>Leicht links.</summary>
    SlightLeft = 16,

    /// <summary>Auffahrt geradeaus.</summary>
    RampStraight = 17,

    /// <summary>Auffahrt rechts.</summary>
    RampRight = 18,

    /// <summary>Auffahrt links.</summary>
    RampLeft = 19,

    /// <summary>Abfahrt rechts.</summary>
    ExitRight = 20,

    /// <summary>Abfahrt links.</summary>
    ExitLeft = 21,

    /// <summary>Geradeaus bleiben.</summary>
    StayStraight = 22,

    /// <summary>Rechts bleiben.</summary>
    StayRight = 23,

    /// <summary>Links bleiben.</summary>
    StayLeft = 24,

    /// <summary>Einordnen.</summary>
    Merge = 25,

    /// <summary>In den Kreisverkehr einfahren.</summary>
    RoundaboutEnter = 26,

    /// <summary>Den Kreisverkehr verlassen.</summary>
    RoundaboutExit = 27,

    /// <summary>Auf die Fähre auffahren.</summary>
    FerryEnter = 28,

    /// <summary>Die Fähre verlassen.</summary>
    FerryExit = 29,

    /// <summary>Öffentlicher Verkehr.</summary>
    Transit = 30,

    /// <summary>Umsteigen.</summary>
    TransitTransfer = 31,

    /// <summary>Sitzen bleiben.</summary>
    TransitRemainOn = 32,

    /// <summary>Beginn der Anbindung an den öffentlichen Verkehr.</summary>
    TransitConnectionStart = 33,

    /// <summary>Umsteigeanbindung.</summary>
    TransitConnectionTransfer = 34,

    /// <summary>Ende der Anbindung an den öffentlichen Verkehr.</summary>
    TransitConnectionDestination = 35,

    /// <summary>Ziel nach der Anbindung an den öffentlichen Verkehr.</summary>
    PostTransitConnectionDestination = 36,

    /// <summary>Rechts einordnen.</summary>
    MergeRight = 37,

    /// <summary>Links einordnen.</summary>
    MergeLeft = 38,

    /// <summary>Aufzug betreten.</summary>
    ElevatorEnter = 39,

    /// <summary>Treppe betreten.</summary>
    StepsEnter = 40,

    /// <summary>Rolltreppe betreten.</summary>
    EscalatorEnter = 41,

    /// <summary>Gebäude betreten.</summary>
    BuildingEnter = 42,

    /// <summary>Gebäude verlassen.</summary>
    BuildingExit = 43
}

/// <summary>Ein Eintrag auf einem Wegweiser.</summary>
/// <param name="Text">Der Beschilderungstext.</param>
/// <param name="ConsecutiveCount">
/// Wie oft dieser Text auf aufeinanderfolgenden Wegweisern wiederholt wird; dient der Gewichtung
/// bei der Anzeige.
/// </param>
public sealed record SignElement(string Text, int? ConsecutiveCount);

/// <summary>
/// Die Beschilderung eines Manövers.
/// </summary>
/// <remarks>
/// Für Autobahnabfahrten ist die Ausfahrtnummer der zuverlässigste Anker für den Fahrer — deutlich
/// verlässlicher als der Straßenname.
/// </remarks>
public class ManeuverSign
{
    /// <summary>Ausfahrtnummern.</summary>
    public List<SignElement>? ExitNumbers { get; set; }

    /// <summary>Aufgeführte weiterführende Straßen.</summary>
    public List<SignElement>? ExitBranches { get; set; }

    /// <summary>Fahrtziele („Richtung …“).</summary>
    public List<SignElement>? ExitToward { get; set; }

    /// <summary>Namen der Ausfahrt.</summary>
    public List<SignElement>? ExitNames { get; set; }
}

/// <summary>
/// Ein einzelnes Fahrmanöver — eine Zeile der Abbiegeanweisung.
/// </summary>
public class NavigationManeuver
{
    /// <summary>Die übersetzte Manöverart.</summary>
    public ManeuverType Type { get; set; }

    /// <summary>
    /// Die von der Routing-Engine gelieferte Rohzahl der Manöverart. Maßgeblich, wenn
    /// <see cref="Type"/> gleich <see cref="ManeuverType.Unknown"/> ist.
    /// </summary>
    public int RawType { get; set; }

    /// <summary>Der Anweisungstext zur Anzeige, z. B. „Rechts auf die Gagarinstraße abbiegen.“</summary>
    public string? Instruction { get; set; }

    /// <summary>Frühzeitiger Sprachhinweis („In 400 Metern …“).</summary>
    public string? VerbalTransitionAlertInstruction { get; set; }

    /// <summary>Kurzfassung des Sprachhinweises für kleine Anzeigen und Sprachausgabe.</summary>
    public string? VerbalSuccinctTransitionInstruction { get; set; }

    /// <summary>Sprachhinweis unmittelbar vor dem Manöver.</summary>
    public string? VerbalPreTransitionInstruction { get; set; }

    /// <summary>Sprachhinweis unmittelbar nach dem Manöver („Der Straße 2 Kilometer folgen.“).</summary>
    public string? VerbalPostTransitionInstruction { get; set; }

    /// <summary>Die Straßennamen des Abschnitts nach dem Manöver.</summary>
    public List<string>? StreetNames { get; set; }

    /// <summary>Die Straßennamen am Beginn des Abschnitts, wenn sie abweichen.</summary>
    public List<string>? BeginStreetNames { get; set; }

    /// <summary>Länge des Abschnitts dieses Manövers in Metern.</summary>
    public double LengthMeters { get; set; }

    /// <summary>Dauer des Abschnitts dieses Manövers in Sekunden.</summary>
    public double TimeSeconds { get; set; }

    /// <summary>
    /// Index des ersten Stützpunkts dieses Manövers in der Geometrie des Abschnitts
    /// (<see cref="NavigationLeg.Shape"/>).
    /// </summary>
    /// <remarks>
    /// Damit lässt sich der noch zu fahrende Teil des Manövers hervorheben, ohne dass der Client
    /// selbst nach der passenden Stelle in der Geometrie suchen muss.
    /// </remarks>
    public int BeginShapeIndex { get; set; }

    /// <summary>Index des letzten Stützpunkts dieses Manövers in der Geometrie des Abschnitts.</summary>
    public int EndShapeIndex { get; set; }

    /// <summary>Fahrtrichtung in Grad unmittelbar vor dem Manöver; fehlt beim ersten Manöver.</summary>
    public int? BearingBefore { get; set; }

    /// <summary>Fahrtrichtung in Grad unmittelbar nach dem Manöver; fehlt beim letzten Manöver.</summary>
    public int? BearingAfter { get; set; }

    /// <summary>Die zu nehmende Ausfahrt eines Kreisverkehrs, von der Einfahrt gezählt.</summary>
    public int? RoundaboutExitCount { get; set; }

    /// <summary>Der Abschnitt enthält eine gebührenpflichtige Strecke.</summary>
    public bool Toll { get; set; }

    /// <summary>Der Abschnitt enthält eine Kraftfahrstraße oder Autobahn.</summary>
    public bool Highway { get; set; }

    /// <summary>Der Abschnitt enthält eine Fährverbindung.</summary>
    public bool Ferry { get; set; }

    /// <summary>Der Abschnitt enthält unbefestigte Strecke.</summary>
    public bool Rough { get; set; }

    /// <summary>Der Abschnitt unterliegt zeitlichen Beschränkungen.</summary>
    public bool HasTimeRestrictions { get; set; }

    /// <summary>Fortbewegungsart, z. B. <c>drive</c>.</summary>
    public string? TravelMode { get; set; }

    /// <summary>Fahrzeugart, z. B. <c>truck</c>.</summary>
    public string? TravelType { get; set; }

    /// <summary>Die Beschilderung des Manövers, sofern vorhanden.</summary>
    public ManeuverSign? Sign { get; set; }
}

/// <summary>Ein Abschnitt einer Fahrt zwischen zwei Haltepunkten.</summary>
public class NavigationLeg
{
    /// <summary>Länge des Abschnitts in Metern.</summary>
    public double LengthMeters { get; set; }

    /// <summary>Dauer des Abschnitts in Sekunden.</summary>
    public double TimeSeconds { get; set; }

    /// <summary>Die Geometrie des Abschnitts als Encoded Polyline (Präzision 1e-6).</summary>
    public string? Shape { get; set; }

    /// <summary>
    /// Die dekodierte Geometrie. Nur befüllt, wenn <c>decodeGeometry</c> angefordert wurde.
    /// </summary>
    public List<GeoPoint>? Geometry { get; set; }

    /// <summary>Die Fahrmanöver des Abschnitts in Fahrtreihenfolge.</summary>
    public List<NavigationManeuver> Maneuvers { get; set; } = new();
}

/// <summary>Das Ergebnis einer Navigationsberechnung.</summary>
public class DirectionsResult
{
    /// <summary>Gesamtlänge in Metern.</summary>
    public double LengthMeters { get; set; }

    /// <summary>Geschätzte Gesamtdauer in Sekunden.</summary>
    public double TimeSeconds { get; set; }

    /// <summary>Die Sprache der Anweisungstexte.</summary>
    public string? Language { get; set; }

    /// <summary>Das verwendete Valhalla-Kostenmodell, z. B. <c>truck</c>.</summary>
    public string Costing { get; set; } = string.Empty;

    /// <summary>
    /// Ob die Hinweise unter Sonderrechten berechnet wurden und damit gegen Verkehrsregeln
    /// verstoßen können.
    /// </summary>
    /// <remarks>
    /// <b>Ist dieser Wert <c>true</c>, muss die Fahreroberfläche das sichtbar kennzeichnen.</b> Der
    /// Fahrer trägt die Verantwortung für die Fahrzeugführung und muss erkennen können, dass ihm
    /// eine Strecke unter Sonderrechten angewiesen wird.
    /// </remarks>
    public bool SovereignRightsApplied { get; set; }

    /// <summary>Die Abschnitte der Fahrt.</summary>
    public List<NavigationLeg> Legs { get; set; } = new();
}

/// <summary>
/// Ein Halt der geplanten Route, angereichert um seine Lage auf der gespeicherten Geometrie.
/// </summary>
public class NavigationStop
{
    /// <summary>Position in der Fahrtreihenfolge (<c>step.sequence</c>).</summary>
    public int Sequence { get; set; }

    /// <summary>Art des Halts.</summary>
    public StepType Type { get; set; }

    /// <summary>Der bediente Sammelpunkt; nur bei <see cref="StepType.Collection"/>.</summary>
    public int? JobId { get; set; }

    /// <summary>Die genutzte Abladestelle; nur bei <see cref="StepType.Disposal"/>.</summary>
    public int? DeliveryDestinationId { get; set; }

    /// <summary>Anzeigename des Halts.</summary>
    public string? Name { get; set; }

    /// <summary>Breitengrad des Halts.</summary>
    public double Latitude { get; set; }

    /// <summary>Längengrad des Halts.</summary>
    public double Longitude { get; set; }

    /// <summary>Geplanter Ankunftszeitpunkt.</summary>
    public DateTimeOffset PlannedArrival { get; set; }

    /// <summary>Geplante kumulierte reine Fahrzeit ab Schichtstart.</summary>
    public TimeSpan PlannedTravelTimeFromStart { get; set; }

    /// <summary>Geplante kumulierte Fahrstrecke ab Schichtstart in Metern.</summary>
    public int PlannedDistanceFromStartMeters { get; set; }

    /// <summary>Bedienzeit an diesem Halt.</summary>
    public TimeSpan ServiceTime { get; set; }

    /// <summary>
    /// Lage des Halts auf der gespeicherten Geometrie, gemessen in Metern ab Streckenbeginn.
    /// </summary>
    /// <remarks>
    /// Dies ist der Wert, mit dem der Fahrer-Client den Fortschritt <em>selbst</em> berechnen kann,
    /// ohne für jede Positionsmeldung den Server zu fragen.
    /// </remarks>
    public double DistanceAlongRouteMeters { get; set; }

    /// <summary>
    /// Abstand des Halts von der gespeicherten Geometrie in Metern.
    /// </summary>
    /// <remarks>
    /// Ein großer Wert ist ein Hinweis auf Datenpflegebedarf: Der Sammelpunkt liegt dann abseits
    /// der befahrenen Strecke, etwa in einem Hinterhof. Für die Anzeige „Sie sind da“ ist das
    /// wichtig, weil der Fahrer den Behälter nicht von der Fahrbahn aus erreicht.
    /// </remarks>
    public double OffsetFromRouteMeters { get; set; }
}

/// <summary>
/// Die Fortschrittsmessung eines Fahrzeugs gegen seine geplante Route.
/// </summary>
public class RouteProgressResult
{
    /// <summary>Kennung der Route, gegen die gemessen wurde.</summary>
    public int RouteId { get; set; }

    /// <summary>Breitengrad des Lotfußpunkts auf der geplanten Strecke.</summary>
    public double SnappedLatitude { get; set; }

    /// <summary>Längengrad des Lotfußpunkts auf der geplanten Strecke.</summary>
    public double SnappedLongitude { get; set; }

    /// <summary>
    /// Querabstand der gemeldeten Position zur geplanten Strecke in Metern.
    /// </summary>
    /// <remarks>Die eigentliche Messgröße. Alles Weitere ist deren Bewertung.</remarks>
    public double LateralOffsetMeters { get; set; }

    /// <summary>Die gemeldete Ortungsgenauigkeit, zur Gewichtung gespiegelt.</summary>
    public double? ReportedAccuracyMeters { get; set; }

    /// <summary>Zurückgelegte Streckenlänge auf der geplanten Route in Metern.</summary>
    public double DistanceAlongRouteMeters { get; set; }

    /// <summary>Gesamtlänge der geplanten Strecke nach ihrer Geometrie in Metern.</summary>
    public double RouteLengthMeters { get; set; }

    /// <summary>
    /// Die vom Optimierer berichtete Fahrstrecke der Route in Metern, sofern gespeichert.
    /// </summary>
    /// <remarks>
    /// Kann geringfügig von <see cref="RouteLengthMeters"/> abweichen — die eine Zahl ist die Summe
    /// der von der Routing-Engine gemeldeten Abschnittslängen, die andere die Länge der abgetasteten
    /// Geometrie. Beide werden ausgewiesen, damit die Abweichung sichtbar ist statt stillschweigend
    /// eine der beiden zu bevorzugen. Die Fortschrittsrechnung verwendet ausschließlich
    /// <see cref="RouteLengthMeters"/>, weil nur diese zur projizierten Lage passt.
    /// </remarks>
    public int? ReportedMeters { get; set; }

    /// <summary>Anteil der bereits zurückgelegten Strecke (0…1).</summary>
    public double ProgressFraction { get; set; }

    /// <summary>Index des Geometrie-Stützpunkts, auf dessen Abschnitt projiziert wurde.</summary>
    public int ShapeIndex { get; set; }

    /// <summary>Ob das Fahrzeug innerhalb der Toleranz auf der geplanten Strecke ist.</summary>
    public bool OnRoute { get; set; }

    /// <summary>Die angewandte Toleranz in Metern.</summary>
    public double OffRouteToleranceMeters { get; set; }

    /// <summary>
    /// Ob eine Neuberechnung angezeigt ist. Entspricht <c>!onRoute</c> für <em>diese eine</em> Messung.
    /// </summary>
    /// <remarks>
    /// <b>Nicht bei der ersten abweichenden Messung neu berechnen.</b> GPS springt. Erst wenn
    /// <see cref="ConfirmationFixesRecommended"/> aufeinanderfolgende Messungen abweichen, ist die
    /// Abweichung belegt. Die Zahl steht hier, damit die Regel serverseitig konfigurierbar bleibt
    /// und nicht in jeder App-Version erneut geraten wird.
    /// </remarks>
    public bool RerouteRecommended { get; set; }

    /// <summary>
    /// Empfohlene Anzahl aufeinanderfolgender abweichender Messungen vor einer Neuberechnung.
    /// </summary>
    public int ConfirmationFixesRecommended { get; set; }

    /// <summary>Der angefahrene Halt; <c>null</c>, wenn kein Halt mehr aussteht.</summary>
    public NavigationStop? NextStop { get; set; }

    /// <summary>Ob der angefahrene Halt aus der Position abgeleitet wurde (statt vorgegeben).</summary>
    public bool NextStopWasInferred { get; set; }

    /// <summary>Reststrecke bis zum angefahrenen Halt entlang der geplanten Route in Metern.</summary>
    public double? RemainingDistanceToNextStopMeters { get; set; }

    /// <summary>Restfahrzeit bis zum angefahrenen Halt.</summary>
    /// <remarks>
    /// Aus dem Plan linear zwischen den Halten interpoliert — es ist die <em>geplante</em>
    /// Restfahrzeit, keine Vorhersage aus der tatsächlich gefahrenen Geschwindigkeit.
    /// </remarks>
    public TimeSpan? RemainingTravelTimeToNextStop { get; set; }

    /// <summary>Luftlinienentfernung zum angefahrenen Halt in Metern.</summary>
    public double? StraightLineDistanceToNextStopMeters { get; set; }

    /// <summary>Ob das Fahrzeug den angefahrenen Halt erreicht hat.</summary>
    public bool AtNextStop { get; set; }

    /// <summary>Der angewandte Ankunftsradius in Metern.</summary>
    public double ArrivalRadiusMeters { get; set; }

    /// <summary>Reststrecke bis zum Schichtende entlang der geplanten Route in Metern.</summary>
    public double RemainingDistanceToRouteEndMeters { get; set; }

    /// <summary>Geplante Restfahrzeit bis zum Schichtende, ohne Bedienzeiten.</summary>
    public TimeSpan RemainingTravelTimeToRouteEnd { get; set; }

    /// <summary>Summe der noch ausstehenden Bedienzeiten.</summary>
    public TimeSpan RemainingServiceTimeToRouteEnd { get; set; }

    /// <summary>Die noch ausstehenden Halte in Fahrtreihenfolge.</summary>
    public List<NavigationStop> RemainingStops { get; set; } = new();
}

/// <summary>Abbiegehinweise zum nächsten Halt samt der zugehörigen Fortschrittsmessung.</summary>
public class RouteGuidanceResult
{
    /// <summary>Der Halt, zu dem die Hinweise führen.</summary>
    public NavigationStop Target { get; set; } = new();

    /// <summary>Die Fortschrittsmessung zum Zeitpunkt der Anfrage.</summary>
    public RouteProgressResult Progress { get; set; } = new();

    /// <summary>Die Abbiegehinweise von der gemeldeten Position zum Halt.</summary>
    public DirectionsResult Directions { get; set; } = new();
}

/// <summary>
/// Die geplante Route in der für eine örtliche Fortschrittsberechnung nötigen Form.
/// </summary>
/// <remarks>
/// <b>Einmal je Schicht abrufen, dann örtlich rechnen.</b> Mit dieser Antwort kann der Fahrer-Client
/// die Position bei jeder Ortung selbst auf die Strecke projizieren und muss den Server nur bei
/// einer Zustandsänderung fragen — bei erkannter Abweichung, bei Ankunft an einem Halt oder in
/// einem groben Zeittakt. Eine Fortschrittsabfrage im Sekundentakt wäre gegenüber der
/// Ratenbegrenzung nicht durchhaltbar und ist auch nicht nötig.
/// </remarks>
public class RoutePlanResult
{
    /// <summary>Kennung der Route.</summary>
    public int RouteId { get; set; }

    /// <summary>Anzeigename der Route.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Beginn des Schichtfensters.</summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>Ende des Schichtfensters.</summary>
    public DateTimeOffset EndDate { get; set; }

    /// <summary>Die geplante Strecke als Encoded Polyline (Präzision 1e-6).</summary>
    public string? Geometry { get; set; }

    /// <summary>
    /// Die dekodierte Strecke. Nur befüllt, wenn angefordert und innerhalb der konfigurierten
    /// Obergrenze.
    /// </summary>
    public List<GeoPoint>? DecodedGeometry { get; set; }

    /// <summary>
    /// Ob die dekodierte Geometrie wegen der Obergrenze weggelassen wurde.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich gemeldet, statt still eine leere Liste zu liefern: „keine Punkte“ und „zu viele
    /// Punkte“ sind für den Client unterschiedliche Lagen.
    /// </remarks>
    public bool DecodedGeometryOmitted { get; set; }

    /// <summary>Anzahl der Stützpunkte der Geometrie.</summary>
    public int GeometryPointCount { get; set; }

    /// <summary>Länge der Strecke nach ihrer Geometrie in Metern.</summary>
    public double GeometryLengthMeters { get; set; }

    /// <summary>Die vom Optimierer berichtete Fahrstrecke in Metern.</summary>
    /// <remarks>
    /// Kann geringfügig von <see cref="GeometryLengthMeters"/> abweichen: Der eine Wert ist die
    /// Summe der von der Routing-Engine gemeldeten Abschnittslängen, der andere die Länge der
    /// abgetasteten Geometrie. Beide werden ausgewiesen, damit die Abweichung sichtbar ist statt
    /// stillschweigend eine der beiden Zahlen zu bevorzugen.
    /// </remarks>
    public int? ReportedMeters { get; set; }

    /// <summary>Die angewandte Abweichungstoleranz in Metern.</summary>
    public double OffRouteToleranceMeters { get; set; }

    /// <summary>Die empfohlene Anzahl bestätigender Messungen vor einer Neuberechnung.</summary>
    public int ConfirmationFixesRecommended { get; set; }

    /// <summary>Der angewandte Ankunftsradius in Metern.</summary>
    public double ArrivalRadiusMeters { get; set; }

    /// <summary>Alle Halte in Fahrtreihenfolge, mit ihrer Lage auf der Geometrie.</summary>
    public List<NavigationStop> Stops { get; set; } = new();
}
