using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Wurzel der Konfiguration des Sammel-Routing-Stacks (Abschnitt <c>"Routing"</c> in
/// <c>appsettings.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// Bewusst vollständig konfigurierbar: Jede Zahl, die das Ergebnis einer Routenberechnung
/// beeinflusst, muss für einen Förder-Audit nachvollziehbar und versionierbar sein. Es gibt in
/// diesem Stack keine im Code versteckten "Magic Numbers" mehr.
/// </para>
/// <para>
/// Alle Werte werden beim Start über <see cref="IValidatableObject"/> geprüft
/// (<c>ValidateOnStart</c>), damit eine fehlerhafte Konfiguration sofort auffällt und nicht erst
/// beim ersten Optimierungslauf zu unplausiblen Routen führt.
/// </para>
/// </remarks>
public sealed class RoutingOptions : IValidatableObject
{
    /// <summary>Der Konfigurationsabschnitt, aus dem diese Optionen gebunden werden.</summary>
    public const string SectionName = "Routing";

    /// <summary>Verbindungsparameter zur selbst gehosteten Valhalla-Instanz.</summary>
    public ValhallaOptions Valhalla { get; init; } = new();

    /// <summary>Fahrzeugprofil (Abmessungen/Gewicht) für das Valhalla-Kostenmodell.</summary>
    public VehicleProfileOptions Vehicle { get; init; } = new();

    /// <summary>Sonderrechte ("Hoheitlich-Modus").</summary>
    public SovereignOptions Sovereign { get; init; } = new();

    /// <summary>Bedienzeiten an Sammelpunkten und Abladestellen.</summary>
    public ServiceTimeOptions ServiceTimes { get; init; } = new();

    /// <summary>Emissionsmodell für die CO2e-Bewertung.</summary>
    public EmissionOptions Emissions { get; init; } = new();

    /// <summary>Parameter des Optimierers.</summary>
    public PlannerOptions Planner { get; init; } = new();

    /// <summary>
    /// Parameter der Fahrer-Navigation (Live-Fortschritt und Abbiegehinweise).
    /// </summary>
    /// <remarks>
    /// Absichtlich <em>hier</em> und nicht in einem eigenen Konfigurationsabschnitt: Navigation und
    /// Planung müssen dasselbe Kostenmodell verwenden. Getrennte Wurzeln hätten genau die
    /// Divergenz zugelassen, die <see cref="ValhallaCostingProfile"/> ausschließt.
    /// </remarks>
    public NavigationOptions Navigation { get; init; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Sonderrechte dürfen niemals ohne dokumentierte Rechtsgrundlage aktiv sein. Das ist eine
        // Governance-Anforderung, keine technische: ein Audit muss die Grundlage benennen können.
        if (Sovereign.Enabled && string.IsNullOrWhiteSpace(Sovereign.LegalBasis))
        {
            yield return new ValidationResult(
                "Routing:Sovereign:LegalBasis muss gesetzt sein, wenn Sonderrechte aktiviert sind.",
                new[] { $"{nameof(Sovereign)}.{nameof(SovereignOptions.LegalBasis)}" });
        }

        // Gewichts- und Größenbeschränkungen zu ignorieren ist eine Gefahr für Brücken und Tunnel
        // und ist von Sonderrechten nach StVO nicht gedeckt.
        if (Sovereign.Enabled && Sovereign.IgnoreRestrictions && !Sovereign.AcknowledgeSafetyRiskOfIgnoreRestrictions)
        {
            yield return new ValidationResult(
                "Routing:Sovereign:IgnoreRestrictions umgeht auch Gewichts-/Höhenbeschränkungen. "
                + "Bevorzugt IgnoreNonVehicularRestrictions verwenden, oder "
                + "AcknowledgeSafetyRiskOfIgnoreRestrictions ausdrücklich auf true setzen.",
                new[] { $"{nameof(Sovereign)}.{nameof(SovereignOptions.IgnoreRestrictions)}" });
        }

        if (Emissions.PayloadUnitKilograms <= 0.0)
        {
            yield return new ValidationResult(
                "Routing:Emissions:PayloadUnitKilograms muss > 0 sein, sonst ist keine CO2e-Bilanz möglich.",
                new[] { $"{nameof(Emissions)}.{nameof(EmissionOptions.PayloadUnitKilograms)}" });
        }

        // Einem Menschen anzuweisen, entgegen der Fahrtrichtung oder durch eine gesperrte Zufahrt zu
        // fahren, ist eine andere Handlung als dieselbe Kante rechnerisch günstiger zu bepreisen.
        // Die Planung darf das mit dokumentierter Rechtsgrundlage; die Fahreranweisung erfordert
        // zusätzlich eine ausdrückliche Bestätigung. Ohne Sonderrechte greift die Regel nicht.
        bool guidanceBreaksTrafficRules =
            Sovereign.Enabled
            && Navigation.ApplySovereignRights
            && (Sovereign.IgnoreOneways || Sovereign.IgnoreRestrictions || Sovereign.IgnoreAccess);

        if (guidanceBreaksTrafficRules && !Navigation.AcknowledgeGuidanceAgainstTrafficRules)
        {
            yield return new ValidationResult(
                "Routing:Navigation:ApplySovereignRights erteilt dem Fahrer Hinweise, die gegen "
                + "Verkehrsregeln verstoßen (Einbahnstraße, Zufahrts- oder sonstige Beschränkungen). "
                + "Entweder Routing:Navigation:AcknowledgeGuidanceAgainstTrafficRules ausdrücklich "
                + "auf true setzen oder Routing:Navigation:ApplySovereignRights auf false — dann "
                + "sind die Hinweise StVO-konform, weichen aber von der geplanten Strecke ab.",
                new[]
                {
                    $"{nameof(Navigation)}.{nameof(NavigationOptions.AcknowledgeGuidanceAgainstTrafficRules)}"
                });
        }

        // Ein Ankunftsradius größer als die Abweichungstoleranz macht die Ankunftserkennung
        // unschärfer als die Off-Route-Erkennung: Das Fahrzeug gälte als angekommen, während es
        // nach derselben Messung noch als abgewichen zählt. Widersprüchliche Zustände sind für den
        // Zustandsautomaten des Fahrer-Clients nicht auflösbar.
        if (Navigation.ArrivalRadiusMeters > Navigation.OffRouteToleranceMeters * 4.0)
        {
            yield return new ValidationResult(
                "Routing:Navigation:ArrivalRadiusMeters ist unplausibel groß im Verhältnis zu "
                + "OffRouteToleranceMeters (mehr als das Vierfache).",
                new[] { $"{nameof(Navigation)}.{nameof(NavigationOptions.ArrivalRadiusMeters)}" });
        }
    }
}

/// <summary>Verbindungs- und Limit-Parameter der Valhalla-Instanz.</summary>
public sealed class ValhallaOptions
{
    /// <summary>
    /// Root URL (scheme, host, port; no path) of the operator's own Valhalla instance.
    /// </summary>
    /// <remarks>
    /// Intentionally without a default: an address compiled into the application would point every
    /// installation at the original operator's host. It must come from configuration
    /// (<c>Routing:Valhalla:BaseUrl</c> / <c>Routing__Valhalla__BaseUrl</c>) and is validated at
    /// startup by <see cref="SmartRoutingAPI.Configuration.DeploymentSettings.RequireServiceRootUrl"/>.
    /// </remarks>
    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Timeout einzelner Valhalla-Aufrufe in Sekunden.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Maximale Anzahl Quellen bzw. Ziele pro <c>/sources_to_targets</c>-Aufruf. Muss zum
    /// <c>max_matrix_locations</c> der Valhalla-Konfiguration passen; größere Matrizen werden
    /// automatisch in Blöcke zerlegt.
    /// </summary>
    [Range(2, 2000)]
    public int MaxMatrixLocationsPerRequest { get; init; } = 50;

    /// <summary>
    /// Maximale Anzahl Stützpunkte pro <c>/route</c>-Aufruf. Längere Routen werden in
    /// überlappende Abschnitte zerlegt und anschließend zu einer Geometrie zusammengefügt.
    /// </summary>
    [Range(2, 1000)]
    public int MaxRouteLocationsPerRequest { get; init; } = 50;
}

/// <summary>
/// Fahrzeugprofil für das Valhalla-Kostenmodell. Die Vorgaben entsprechen einem dreiachsigen
/// Abfallsammelfahrzeug (26 t).
/// </summary>
public sealed class VehicleProfileOptions
{
    /// <summary>
    /// Valhalla-Kostenmodell. Für Sammelfahrzeuge ist <c>truck</c> korrekt, weil nur dieses
    /// Modell Zufahrtsbeschränkungen, Breiten-, Höhen- und Gewichtsgrenzen auswertet.
    /// </summary>
    [Required]
    public string Costing { get; init; } = "truck";

    /// <summary>Fahrzeughöhe in Metern.</summary>
    [Range(1.0, 6.0)]
    public double HeightMeters { get; init; } = 3.6;

    /// <summary>Fahrzeugbreite in Metern.</summary>
    [Range(1.0, 4.0)]
    public double WidthMeters { get; init; } = 2.55;

    /// <summary>Fahrzeuglänge in Metern.</summary>
    [Range(1.0, 30.0)]
    public double LengthMeters { get; init; } = 9.5;

    /// <summary>Zulässiges Gesamtgewicht in Tonnen.</summary>
    [Range(0.5, 60.0)]
    public double WeightTonnes { get; init; } = 26.0;

    /// <summary>Achslast in Tonnen.</summary>
    [Range(0.5, 20.0)]
    public double AxleLoadTonnes { get; init; } = 9.0;

    /// <summary>Anzahl der Achsen.</summary>
    [Range(2, 10)]
    public int AxleCount { get; init; } = 3;

    /// <summary>Bauartbedingte Höchstgeschwindigkeit in km/h.</summary>
    [Range(10, 130)]
    public int TopSpeedKph { get; init; } = 80;

    /// <summary>
    /// Valhallas <c>use_distance</c>-Faktor: 0 optimiert ausschließlich auf Zeit, 1 ausschließlich
    /// auf Entfernung. Da CO2 mit der Entfernung und nicht mit der Fahrzeit korreliert, ist für
    /// eine CO2-Zielsetzung ein Wert deutlich über 0 erforderlich.
    /// </summary>
    [Range(0.0, 1.0)]
    public double UseDistance { get; init; } = 0.6;

    /// <summary>
    /// Strafzeit (Sekunden) für Straßen ohne LKW-Zufahrt. Der Valhalla-Standard 43200 verbietet
    /// solche Straßen effektiv. Die Abfallsammlung muss Wohnstraßen anfahren, daher ein endlicher,
    /// aber deutlicher Wert.
    /// </summary>
    [Range(0, 43200)]
    public int HgvNoAccessPenaltySeconds { get; init; } = 300;
}

/// <summary>
/// Sonderrechte des Sammelfahrzeugs ("Hoheitlich-Modus").
/// </summary>
/// <remarks>
/// Diese Optionen erlauben dem Router, Verkehrsregeln zu übergehen. Sie sind bewusst
/// standardmäßig deaktiviert und müssen pro Auftraggeber ausdrücklich freigegeben,
/// mit Rechtsgrundlage dokumentiert und protokolliert werden.
/// </remarks>
public sealed class SovereignOptions
{
    /// <summary>Schaltet den Hoheitlich-Modus insgesamt ein. Standard: aus.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Dokumentierte Rechtsgrundlage (z. B. die einschlägige StVO-Vorschrift samt Freigabe des
    /// Auftraggebers). Pflichtangabe, sobald <see cref="Enabled"/> gesetzt ist; wird bei jedem
    /// Optimierungslauf mitprotokolliert.
    /// </summary>
    public string LegalBasis { get; init; } = string.Empty;

    /// <summary>Einbahnstraßen dürfen entgegen der Fahrtrichtung befahren werden.</summary>
    public bool IgnoreOneways { get; init; } = true;

    /// <summary>
    /// Abbiege- und bedingte Beschränkungen werden ignoriert, Gewichts- und Größenbeschränkungen
    /// bleiben jedoch wirksam. Das ist die sichere Variante und die empfohlene Vorgabe.
    /// </summary>
    public bool IgnoreNonVehicularRestrictions { get; init; } = true;

    /// <summary>
    /// Ignoriert <em>alle</em> Beschränkungen, einschließlich Gewicht und Höhe. Gefährlich:
    /// kann 26-t-Fahrzeuge über Brücken mit 7,5-t-Begrenzung führen. Standard: aus.
    /// </summary>
    public bool IgnoreRestrictions { get; init; }

    /// <summary>Ausdrückliche Bestätigung des Sicherheitsrisikos von <see cref="IgnoreRestrictions"/>.</summary>
    public bool AcknowledgeSafetyRiskOfIgnoreRestrictions { get; init; }

    /// <summary>Ignoriert verkehrsmittelbezogene Zufahrtsbeschränkungen (<c>access</c>-Tags).</summary>
    public bool IgnoreAccess { get; init; }
}

/// <summary>Bedienzeiten. Ohne realistische Bedienzeiten ist jede Schichtplanung wertlos.</summary>
public sealed class ServiceTimeOptions
{
    /// <summary>Grundzeit pro Sammelpunkt in Sekunden (Anfahren, Aufnehmen, Absetzen).</summary>
    [Range(0, 7200)]
    public int ContainerBaseSeconds { get; init; } = 420;

    /// <summary>
    /// Zusätzliche Bedienzeit je Kapazitätseinheit Sammelgut in Sekunden.
    /// </summary>
    /// <remarks>
    /// Die Vorgabe ist bewusst <c>0</c>: Die Kapazitätseinheit ist im Datenmodell frei definiert
    /// (Kilogramm, Kubikmeter, Behälterzahl). Ein von null verschiedener Wert ergibt nur Sinn,
    /// wenn er zur gewählten Einheit passt – bei Kilogramm wären 120 Sekunden je Einheit grob
    /// falsch. Die Grundzeit deckt den Regelfall ab.
    /// </remarks>
    [Range(0, 7200)]
    public int ContainerSecondsPerUnit { get; init; }

    /// <summary>Bedienzeit an einer Abladestelle in Sekunden (Wiegen, Entleeren, Rangieren).</summary>
    [Range(0, 14400)]
    public int FacilitySeconds { get; init; } = 900;
}

/// <summary>
/// Parameter des CO2e-Modells.
/// </summary>
/// <remarks>
/// Die Vorgaben orientieren sich an HBEFA-Größenordnungen für Abfallsammelfahrzeuge im
/// Stadtverkehr. Sie sind <em>Startwerte</em> und müssen vom Betreiber gegen den eigenen
/// Fuhrpark kalibriert werden; <see cref="FactorSource"/> dokumentiert die verwendete Quelle
/// und wird in die Berechnung mit ausgegeben.
/// </remarks>
public sealed class EmissionOptions
{
    /// <summary>Nachweisbare Quelle der Faktoren (erscheint in der Bilanz).</summary>
    [Required]
    public string FactorSource { get; init; } = "Startwerte in HBEFA-Größenordnung – nicht kalibriert";

    /// <summary>Wie viele Kilogramm eine Kapazitätseinheit darstellt. Basis der gesamten Massenbilanz.</summary>
    [Range(0.001, 100000.0)]
    public double PayloadUnitKilograms { get; init; } = 1.0;

    /// <summary>CO2e-Ausstoß des leeren Fahrzeugs in Gramm pro Kilometer.</summary>
    [Range(0.0, 10000.0)]
    public double BaseGramsPerKm { get; init; } = 900.0;

    /// <summary>Zusätzlicher CO2e-Ausstoß in Gramm pro Kilometer und Kilogramm Zuladung.</summary>
    [Range(0.0, 10.0)]
    public double GramsPerKmPerKgPayload { get; init; } = 0.0225;

    /// <summary>CO2e-Ausstoß eines Sammelhalts in Gramm (Leerlauf, Aufbauhydraulik, Anfahren).</summary>
    [Range(0.0, 100000.0)]
    public double GramsPerStop { get; init; } = 250.0;

    /// <summary>
    /// CO2e-Ausstoß einer Entleerung an einer Abladestelle in Gramm.
    /// </summary>
    /// <remarks>
    /// Eine Entleerung umfasst Anmelden, Wiegen, Kippen, Rangieren und längeren Leerlauf und ist
    /// damit deutlich aufwendiger als ein Sammelhalt. Zusammen mit der Bedienzeit der Abladestelle
    /// ist dieser Wert der Hebel, mit dem sich zusätzliche Zwischenentleerungen verteuern lassen.
    /// </remarks>
    [Range(0.0, 1000000.0)]
    public double GramsPerDisposalStop { get; init; } = 1500.0;

    /// <summary>
    /// Angenommener mittlerer Beladungsgrad (0…1) für die <em>Zielfunktion</em> des Optimierers.
    /// </summary>
    /// <remarks>
    /// Die Bogenkosten in OR-Tools sind zustandsunabhängig, echte lastabhängige Emissionen lassen
    /// sich dort nicht abbilden. Optimiert wird daher mit diesem Mittelwert; die <em>berichtete</em>
    /// Bilanz wird anschließend mit dem tatsächlichen Lastverlauf der Lösung gerechnet.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double AveragePayloadFactorForObjective { get; init; } = 0.5;
}

/// <summary>Parameter des Optimierers.</summary>
public sealed class PlannerOptions
{
    /// <summary>
    /// Maximale Anzahl Entleerungen an Abladestellen je Schicht. Bestimmt, wie viele
    /// Abladestellen-Knoten im Modell angelegt werden, und begrenzt damit die Modellgröße.
    /// </summary>
    [Range(0, 40)]
    public int MaxReloadsPerShift { get; init; } = 6;

    /// <summary>Rechenzeitbudget des Optimierers in Sekunden.</summary>
    [Range(1, 3600)]
    public int SolverTimeSeconds { get; init; } = 20;

    /// <summary>
    /// Obergrenze der Sammelpunkte, die in einen Optimierungslauf eingehen. Schützt vor
    /// Matrizen, die Valhalla oder den Speicher überlasten.
    /// </summary>
    [Range(1, 2000)]
    public int MaxContainersConsidered { get; init; } = 400;

    /// <summary>
    /// Schattenpreis der Sammlung: Wert in Gramm CO2e, den das Einsammeln einer Kapazitätseinheit
    /// rechtfertigt.
    /// </summary>
    /// <remarks>
    /// Dies ist der zentrale Effizienzregler bei <c>FillShift = false</c>: Ein Sammelpunkt wird nur
    /// angefahren, wenn die dadurch zusätzlich verursachten Emissionen unter diesem Wert liegen.
    /// Genau dieses Grenzkosten-Kriterium fehlte der alten Kandidatenauswahl, die stattdessen die
    /// Schicht gefüllt hat.
    /// </remarks>
    [Range(0.0, 1000000.0)]
    public double CollectionValueGramsPerUnit { get; init; } = 3000.0;

    /// <summary>
    /// Das Fahrzeug muss leer zum Endpunkt zurückkehren, erzwingt also einen abschließenden
    /// Besuch einer Abladestelle. Betrieblich in der Glassammlung der Normalfall.
    /// </summary>
    public bool RequireEmptyOnReturn { get; init; } = true;

    /// <summary>
    /// Anteil der Schicht, der planbar verfügbar ist (Rüstzeiten, Pausen, Puffer). 0,9 bedeutet:
    /// 90 % der Fensterlänge dürfen verplant werden.
    /// </summary>
    [Range(0.1, 1.0)]
    public double UsableShiftFraction { get; init; } = 0.9;

    /// <summary>
    /// Untergrenze des Prioritätsgewichts. Verhindert, dass Sammelpunkte mit Priorität 0 – etwa
    /// frisch angelegte Standorte – grundsätzlich nie bedient werden.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinimumPriorityFactor { get; init; } = 0.25;

    /// <summary>
    /// Anzahl der Nachkommastellen, mit denen Sammelmengen im Optimierer geführt werden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ganzzahlige Optimierer – sowohl OR-Tools als auch VROOM – rechnen ausschließlich mit ganzen
    /// Zahlen. Mengen wie 1,2 Kapazitätseinheiten je Container müssen daher intern skaliert werden;
    /// bei drei Nachkommastellen wird aus 1,2 der Wert 1200. Ohne diese Skalierung würde jeder
    /// Behälter auf 1 abgeschnitten, und bereits nach fünf Behältern fehlte eine ganze Einheit.
    /// </para>
    /// <para>
    /// Die Skalierung ist reine Rechenmechanik und erscheint <b>nicht</b> in der API: Ladungen
    /// werden immer in echten Kapazitätseinheiten ausgeliefert. Höhere Werte erhöhen die Genauigkeit
    /// und verringern die darstellbare Höchstmenge entsprechend.
    /// </para>
    /// </remarks>
    [Range(0, 6)]
    public int CapacityDecimals { get; init; } = 3;

    /// <summary>
    /// Führt nach der Auswahl der Sammelpunkte einen zweiten Optimierungsdurchgang über die
    /// <em>festgelegte</em> Menge aus. Vorgabe: ein.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Der erste Durchgang muss gleichzeitig entscheiden, <b>welche</b> Punkte bedient werden und
    /// <b>in welcher Reihenfolge</b>. Bei mehreren hundert wählbaren Punkten verbraucht die
    /// Auswahlentscheidung einen großen Teil des Zeitbudgets, sodass für die Reihenfolge zu wenig
    /// bleibt. Steht die Menge fest, fließt dasselbe Budget vollständig in die Reihenfolge.
    /// </para>
    /// <para>
    /// Das erklärt, warum eine Neuberechnung derselben Route bislang regelmäßig besser ausfiel als
    /// die Erstberechnung. Der Durchgang verdoppelt die Rechenzeit und wird nur übernommen, wenn er
    /// messbar besser ist; eine Verschlechterung ist ausgeschlossen.
    /// </para>
    /// </remarks>
    public bool RefineSelection { get; init; } = true;
}
