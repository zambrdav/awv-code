namespace SmartRoutingAPI.Services.Routing;

/// <summary>Ein möglicher Sammelpunkt (z. B. ein Glascontainer-Standort).</summary>
/// <param name="Id">Fachliche Kennung (die Job-ID).</param>
/// <param name="Location">Position des Sammelpunkts.</param>
/// <param name="Units">Erwartete Sammelmenge in Kapazitätseinheiten.</param>
/// <param name="Priority">Priorität 0–100. Steuert, wie wertvoll das Anfahren ist.</param>
/// <param name="Label">Anzeigename für die Schrittliste.</param>
/// <param name="Mandatory">
/// Verbindlich zugeordnet: Der Punkt wurde ausdrücklich vom Disponenten gesetzt und soll nach
/// Möglichkeit in jedem Fall bedient werden. Er bleibt technisch auslassbar, damit eine überfüllte
/// Schicht nicht in einer unlösbaren Aufgabe endet – was ausgelassen wurde, wird zurückgemeldet.
/// </param>
/// <param name="ServiceSecondsOverride">
/// Punktgenaue Bedienzeit in Sekunden; <c>null</c> verwendet die globale Vorgabe.
/// </param>
public sealed record CollectionCandidate(
    int Id,
    Coordinate Location,
    double Units,
    int Priority,
    string? Label,
    bool Mandatory = false,
    int? ServiceSecondsOverride = null);

/// <summary>Eine Abladestelle, an der das Fahrzeug entleert werden kann.</summary>
/// <param name="Id">Fachliche Kennung (die DeliveryDestination-ID).</param>
/// <param name="Location">Position der Abladestelle.</param>
/// <param name="MaximumUnits">
/// Aufnahmefähigkeit in Kapazitätseinheiten. Werte kleiner oder gleich null bedeuten unbegrenzt.
/// </param>
/// <param name="Label">Anzeigename für die Schrittliste.</param>
/// <param name="ServiceSecondsOverride">
/// Feste Bedienzeit einer Entleerung in Sekunden; <c>null</c> verwendet die globale Vorgabe.
/// </param>
/// <param name="ServiceSecondsPerUnit">
/// Zusätzliche Bedienzeit je abgeladener Kapazitätseinheit in Sekunden.
/// </param>
/// <remarks>
/// Die Bedienzeit einer Abladestelle bestimmt maßgeblich, wie oft der Optimierer zwischenentleert.
/// Ist sie zu niedrig angesetzt, erscheint eine zusätzliche Entleerung nahezu kostenlos.
/// </remarks>
public sealed record DisposalSite(
    int Id,
    Coordinate Location,
    double MaximumUnits,
    string? Label,
    int? ServiceSecondsOverride = null,
    double? ServiceSecondsPerUnit = null);

/// <summary>Die Aufgabenstellung für einen Planungslauf.</summary>
public sealed class CollectionPlanRequest
{
    /// <summary>Startpunkt der Schicht (Betriebshof).</summary>
    public required Coordinate ShiftStart { get; init; }

    /// <summary>Endpunkt der Schicht.</summary>
    public required Coordinate ShiftEnd { get; init; }

    /// <summary>Beginn des Schichtfensters.</summary>
    public required DateTimeOffset ShiftStartTime { get; init; }

    /// <summary>Ende des Schichtfensters.</summary>
    public required DateTimeOffset ShiftEndTime { get; init; }

    /// <summary>Nutzlast des Fahrzeugs in Kapazitätseinheiten.</summary>
    public required double VehicleCapacityUnits { get; init; }

    /// <summary>
    /// Wenn <c>true</c>, wird die Schicht bewusst so weit gefüllt wie möglich: Es werden so viele
    /// Sammelpunkte bedient, wie im Schichtfenster unterzubringen sind – und zwar in der
    /// emissionsgünstigsten Reihenfolge. Wenn <c>false</c>, wird ein Sammelpunkt nur bedient, wenn
    /// sein Nutzen die zusätzlich verursachten Emissionen übersteigt.
    /// </summary>
    /// <remarks>
    /// Dieses Kennzeichen ist der <em>einzige</em> Unterschied zwischen den beiden Betriebsarten.
    /// Es verändert ausschließlich die Straffunktion für ausgelassene Punkte, nicht das Modell.
    /// Damit ist "Schicht füllen" keine Verschlechterung der Route mehr, sondern eine bewusste
    /// betriebliche Entscheidung bei unverändert optimaler Reihenfolge.
    /// </remarks>
    public bool FillShift { get; init; }

    /// <summary>Die in Frage kommenden Sammelpunkte.</summary>
    public required IReadOnlyList<CollectionCandidate> Candidates { get; init; }

    /// <summary>Die verfügbaren Abladestellen.</summary>
    public required IReadOnlyList<DisposalSite> DisposalSites { get; init; }
}

/// <summary>Die Art eines geplanten Halts.</summary>
public enum PlannedStopKind
{
    /// <summary>Schichtbeginn am Betriebshof.</summary>
    Start = 0,

    /// <summary>Ein Sammelpunkt wird geleert.</summary>
    Collection = 1,

    /// <summary>Das Fahrzeug wird an einer Abladestelle entleert.</summary>
    Disposal = 2,

    /// <summary>Schichtende.</summary>
    End = 3
}

/// <summary>Ein einzelner Halt der geplanten Route.</summary>
/// <param name="Kind">Art des Halts.</param>
/// <param name="ReferenceId">
/// Fachliche Kennung des Sammelpunkts bzw. der Abladestelle; <c>null</c> für Start und Ende.
/// </param>
/// <param name="Label">Anzeigename.</param>
/// <param name="Location">Position des Halts.</param>
/// <param name="ArrivalOffset">Zeitversatz der Ankunft gegenüber dem Schichtbeginn.</param>
/// <param name="Service">Bedienzeit an diesem Halt.</param>
/// <param name="LoadAfterUnits">
/// Ladung nach Abschluss dieses Halts, also die Ladung, die auf dem folgenden Abschnitt
/// transportiert wird. Grundlage der lastabhängigen CO2e-Bilanz.
/// </param>
public sealed record PlannedStop(
    PlannedStopKind Kind,
    int? ReferenceId,
    string? Label,
    Coordinate Location,
    TimeSpan ArrivalOffset,
    TimeSpan Service,
    double LoadAfterUnits);

/// <summary>Das Ergebnis eines Planungslaufs.</summary>
/// <param name="Stops">Die Halte in Fahrtreihenfolge, einschließlich Start und Ende.</param>
/// <param name="CollectedCandidateIds">Die bedienten Sammelpunkte in Reihenfolge der Anfahrt.</param>
/// <param name="SkippedCandidateIds">Punkte, die bewusst nicht bedient wurden (Nutzen unter Kosten).</param>
/// <param name="UnreachableCandidateIds">Punkte, die vom Straßennetz nicht erreichbar sind.</param>
/// <param name="EstimatedMeters">Streckenlänge nach Matrix in Metern (Feinwert folgt aus der Geometrie).</param>
/// <param name="TravelDuration">Reine Fahrzeit.</param>
/// <param name="ServiceDuration">Summe aller Bedienzeiten.</param>
/// <param name="CollectedUnits">Eingesammelte Menge in Kapazitätseinheiten.</param>
/// <param name="ReloadCount">Anzahl der Entleerungen an Abladestellen.</param>
/// <param name="SolverStatus">Status des Optimierers, für Diagnose und Nachweis.</param>
public sealed record CollectionPlan(
    IReadOnlyList<PlannedStop> Stops,
    IReadOnlyList<int> CollectedCandidateIds,
    IReadOnlyList<int> SkippedCandidateIds,
    IReadOnlyList<int> UnreachableCandidateIds,
    int EstimatedMeters,
    TimeSpan TravelDuration,
    TimeSpan ServiceDuration,
    double CollectedUnits,
    int ReloadCount,
    string SolverStatus);

/// <summary>
/// Plant eine Sammeltour: Auswahl der Sammelpunkte, Reihenfolge und Zwischenentleerungen.
/// </summary>
public interface ICollectionPlanner
{
    /// <summary>Berechnet einen Sammelplan.</summary>
    /// <param name="request">Die Aufgabenstellung.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <returns>Der Plan; <c>null</c>, wenn keine zulässige Lösung existiert.</returns>
    /// <exception cref="RoutingEngineException">Wenn das Straßennetz keine Matrix liefern kann.</exception>
    Task<CollectionPlan?> PlanAsync(CollectionPlanRequest request, CancellationToken cancellationToken = default);
}
