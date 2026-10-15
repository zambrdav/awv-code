using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Parameter der Fahrer-Navigation (Abschnitt <c>Routing:Navigation</c>).
/// </summary>
/// <remarks>
/// <para>
/// Bewusst im selben Konfigurationsbaum wie die Planung: Navigation und Planung teilen das
/// Kostenmodell, und eine getrennte Konfigurationswurzel hätte genau die Divergenz erlaubt, die
/// <see cref="ValhallaCostingProfile"/> verhindert. Alle Werte werden beim Start geprüft
/// (<c>ValidateOnStart</c> über <see cref="RoutingOptions"/>).
/// </para>
/// <para>
/// Die Schwellwerte sind <em>Messgrenzen</em>, keine Zustandsautomaten. Die API bleibt zustandslos:
/// Sie meldet die gemessene Abweichung und ihre Bewertung, der Fahrer-Client entscheidet über die
/// Neuberechnung. Ein serverseitiger Zustand je Fahrzeug wäre bei mehreren API-Instanzen sofort
/// falsch — genau der Mangel, der beim Auftragsstatus bereits dokumentiert ist.
/// </para>
/// </remarks>
public sealed class NavigationOptions
{
    /// <summary>
    /// Sprache der Abbiegehinweise als BCP-47-Kennzeichen, z. B. <c>de-DE</c>.
    /// </summary>
    /// <remarks>
    /// Valhalla erzeugt die Anweisungstexte serverseitig. Ohne diese Angabe antwortet es auf
    /// Englisch, und der Fahrer erhält „Turn right onto Gagarinstraße“.
    /// </remarks>
    [Required]
    [MaxLength(35)]
    public string Language { get; init; } = "de-DE";

    /// <summary>
    /// Ob die Fahrhinweise dieselben Sonderrechte verwenden wie die Planung.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vorgabe <c>true</c>: Der Fahrhinweis folgt derselben Kostenbasis wie der Plan. Andernfalls
    /// führt die Navigation den Fahrer auf eine andere Strecke als die geplante, und die Bilanz aus
    /// geplanten gegen gefahrene Kilometer verliert ihre Aussagekraft.
    /// </para>
    /// <para>
    /// <b>Diese Vorgabe ist eine Haftungsentscheidung des Betreibers.</b> Solange
    /// <see cref="SovereignOptions.Enabled"/> aus ist — der Auslieferungszustand —, hat sie keine
    /// Wirkung. Wird der Hoheitlich-Modus eingeschaltet, weist die Navigation einen <em>Menschen</em>
    /// an, gegen Verkehrsregeln zu fahren; das ist von der reinen Wegekostenrechnung zu
    /// unterscheiden und muss über
    /// <see cref="AcknowledgeGuidanceAgainstTrafficRules"/> ausdrücklich bestätigt werden.
    /// </para>
    /// </remarks>
    public bool ApplySovereignRights { get; init; } = true;

    /// <summary>
    /// Ausdrückliche Bestätigung, dass Fahrhinweise entgegen Verkehrsregeln erteilt werden dürfen.
    /// </summary>
    /// <remarks>
    /// Erforderlich, sobald <see cref="SovereignOptions.Enabled"/> und
    /// <see cref="ApplySovereignRights"/> zusammen mit einer regelübergehenden Option aktiv sind.
    /// Ohne Bestätigung verweigert die Anwendung den Start — dieselbe Governance-Regel wie bei
    /// <see cref="SovereignOptions.AcknowledgeSafetyRiskOfIgnoreRestrictions"/>.
    /// </remarks>
    public bool AcknowledgeGuidanceAgainstTrafficRules { get; init; }

    /// <summary>
    /// Zulässiger Querabstand zur geplanten Strecke in Metern, bis zu dem das Fahrzeug als „auf der
    /// Route“ gilt.
    /// </summary>
    /// <remarks>
    /// 35 m deckt zweispurige Fahrbahnen mit getrennten Richtungsgeometrien und übliches
    /// Stadtrauschen (Häuserschluchten, Tunnelausfahrten) ab, ohne eine echte Abweichung in die
    /// Nebenstraße zu verschlucken.
    /// </remarks>
    [Range(5.0, 500.0)]
    public double OffRouteToleranceMeters { get; init; } = 35.0;

    /// <summary>
    /// Anzahl aufeinanderfolgender Messpunkte außerhalb der Toleranz, die der Client abwarten
    /// soll, bevor er neu berechnet.
    /// </summary>
    /// <remarks>
    /// <b>Eine einzelne Positionsmeldung ist kein Beweis.</b> GPS springt; eine Neuberechnung bei
    /// jedem Ausreißer erzeugt ein flackerndes Navigationsbild und unnötige Last auf der
    /// Routing-Infrastruktur. Der Wert wird dem Client in der Antwort mitgeteilt, damit die
    /// Entscheidungsregel an <em>einer</em> Stelle konfiguriert ist und nicht in jeder App-Version
    /// erneut geraten wird.
    /// </remarks>
    [Range(1, 20)]
    public int OffRouteConfirmationFixes { get; init; } = 3;

    /// <summary>
    /// Abstand in Metern, ab dem ein Halt als erreicht gilt.
    /// </summary>
    /// <remarks>
    /// Ein Glascontainer steht regelmäßig einige Meter neben der Fahrbahnachse; das Fahrzeug hält
    /// zudem vor und nicht auf dem Punkt.
    /// </remarks>
    [Range(5.0, 500.0)]
    public double ArrivalRadiusMeters { get; init; } = 40.0;

    /// <summary>
    /// Höchstzahl an Zwischenpunkten je Navigationsanfrage (ohne Start und Ziel).
    /// </summary>
    /// <remarks>
    /// Begrenzt die Last je Anfrage. Eine Fahrer-Navigation führt zum <em>nächsten</em> Halt; wer
    /// zwanzig Zwischenziele anfragt, plant und navigiert nicht.
    /// </remarks>
    [Range(0, 48)]
    public int MaxIntermediateWaypoints { get; init; } = 10;

    /// <summary>
    /// Höchstzahl gemeldeter Sperrstellen (<c>exclude_locations</c>) je Anfrage.
    /// </summary>
    /// <remarks>
    /// Valhallas eigene Vorgabe für <c>max_exclude_locations</c> liegt bei 50. Ein höherer Wert
    /// würde serverseitig ohnehin abgewiesen.
    /// </remarks>
    [Range(0, 50)]
    public int MaxExcludeLocations { get; init; } = 50;

    /// <summary>Höchstzahl gemeldeter Sperrflächen (<c>exclude_polygons</c>) je Anfrage.</summary>
    [Range(0, 20)]
    public int MaxExcludePolygons { get; init; } = 4;

    /// <summary>Höchstzahl Eckpunkte je Sperrfläche.</summary>
    [Range(3, 1000)]
    public int MaxExcludePolygonVertices { get; init; } = 200;

    /// <summary>
    /// Toleranz in Grad, mit der die gemeldete Fahrtrichtung auf eine Straße abgebildet wird.
    /// </summary>
    /// <remarks>
    /// Die Fahrtrichtung ist der Grund, aus dem eine Neuberechnung nicht sofort eine Wende
    /// anweist: Ohne sie kann Valhalla das Fahrzeug der Gegenrichtung derselben Straße zuordnen.
    /// 60° ist auch Valhallas eigene Vorgabe.
    /// </remarks>
    [Range(0, 180)]
    public int HeadingToleranceDegrees { get; init; } = 60;

    /// <summary>
    /// Höchstzahl an Stützpunkten der geplanten Geometrie, die
    /// <c>GET /api/Navigation/routes/{id}/plan</c> dekodiert zurückgibt.
    /// </summary>
    /// <remarks>
    /// Schutz vor sehr großen Antworten. Die kodierte Polyline wird immer mitgeliefert und ist
    /// clientseitig verlustfrei dekodierbar; die dekodierte Fassung ist reine Bequemlichkeit.
    /// </remarks>
    [Range(0, 200000)]
    public int MaxDecodedPlanPoints { get; init; } = 20000;
}
