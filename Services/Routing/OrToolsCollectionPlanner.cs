using Google.OrTools.ConstraintSolver;
using Microsoft.Extensions.Options;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Sammeltourenplanung mit Google OR-Tools.
/// </summary>
/// <remarks>
/// <para>
/// Die Aufgabe ist ein <em>Capacitated VRP with Intermediate Facilities</em>: Das Fahrzeug sammelt
/// ein, muss zwischendurch mehrfach entleeren und darf dazu <b>eine beliebige</b> der vorhandenen
/// Abladestellen wählen. VROOM kann das strukturell nicht abbilden – es kennt keine
/// Alternativstandorte für Aufgaben, wie die Projektbetreuung von VROOM ausdrücklich bestätigt hat.
/// Genau diese Fähigkeit liefert OR-Tools über <c>AddDisjunction</c>.
/// </para>
/// <para>Das Modell besteht aus vier Bausteinen:</para>
/// <list type="number">
///   <item><description>
///     <b>Zielfunktion = CO2e.</b> Die Bogenkosten sind Emissionen, nicht Fahrzeit. Damit
///     optimiert der Planer genau die Größe, die auch berichtet wird.
///   </description></item>
///   <item><description>
///     <b>Schichtdauer als eigene Dimension.</b> Zeit ist eine Nebenbedingung, keine Zielgröße.
///     Diese Trennung von Kosten und Beschränkung war mit VROOM nicht möglich, dessen .NET-Wrapper
///     ausschließlich eine <c>durations</c>-Matrix überträgt und daraus auch die Kosten bildet.
///   </description></item>
///   <item><description>
///     <b>Abladestellen als optionale, vervielfachte Knoten.</b> Jede Kopie ist ein möglicher
///     Entleerungsvorgang; die Ladung wird dort exakt auf null zurückgesetzt. Die Auswahl,
///     <em>welche</em> Abladestelle wann genutzt wird, trifft der Optimierer.
///   </description></item>
///   <item><description>
///     <b>Sammelpunkte als optionale Knoten mit Straffunktion.</b> Damit ersetzt der Optimierer die
///     frühere heuristische Kandidatenauswahl vollständig – einschließlich der Schleife, die die
///     Route so lange vergrößerte, bis 80 % der Schicht verbraucht waren.
///   </description></item>
/// </list>
/// </remarks>
public sealed class OrToolsCollectionPlanner : ICollectionPlanner
{
    private const string LoadDimension = "Load";
    private const string TimeDimension = "Time";

    /// <summary>
    /// Obergrenze für skalierte Kapazitäten. Verhindert den stillen Überlauf, der in der
    /// Vorversion durch <c>(int)capacity * 1000</c> möglich war.
    /// </summary>
    private const long MaxScaledCapacity = 1_000_000_000_000L;

    /// <summary>Straffunktion für ausgelassene Punkte im Modus "Schicht füllen".</summary>
    private const long FillShiftDropPenalty = 1_000_000_000L;

    private readonly IRoadNetworkService _roadNetwork;
    private readonly IEmissionModel _emissions;
    private readonly RoutingOptions _options;
    private readonly ILogger<OrToolsCollectionPlanner> _logger;

    /// <summary>Nachkommastellen, mit denen Mengen intern geführt werden.</summary>
    private readonly int _capacityDecimals;

    /// <summary>
    /// Ganzzahl-Skalierung der Kapazitätseinheiten für den Optimierer, abgeleitet aus
    /// <see cref="PlannerOptions.CapacityDecimals"/>.
    /// </summary>
    /// <remarks>
    /// Ausschließlich intern: Der Optimierer rechnet ganzzahlig, die API liefert Ladungen dagegen
    /// stets in echten Kapazitätseinheiten. Ein Skalierungsfaktor darf niemals nach außen gelangen –
    /// genau daraus entstand die Notwendigkeit, im Frontend durch 1000 zu teilen.
    /// </remarks>
    private readonly long _unitScale;

    /// <summary>Erzeugt den Planer.</summary>
    /// <param name="roadNetwork">Zugang zum Straßennetz (Matrix mit Sonderrechte-Profil).</param>
    /// <param name="emissions">Das CO2e-Modell, das die Zielfunktion bildet.</param>
    /// <param name="options">Die validierte Routing-Konfiguration.</param>
    /// <param name="logger">Logger für Diagnose.</param>
    public OrToolsCollectionPlanner(
        IRoadNetworkService roadNetwork,
        IEmissionModel emissions,
        IOptions<RoutingOptions> options,
        ILogger<OrToolsCollectionPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _roadNetwork = roadNetwork ?? throw new ArgumentNullException(nameof(roadNetwork));
        _emissions = emissions ?? throw new ArgumentNullException(nameof(emissions));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _capacityDecimals = Math.Clamp(_options.Planner.CapacityDecimals, 0, 6);
        _unitScale = (long)Math.Pow(10, _capacityDecimals);
    }

    /// <inheritdoc />
    public async Task<CollectionPlan?> PlanAsync(
        CollectionPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);

            long shiftSeconds = ComputeUsableShiftSeconds(request);
            if (shiftSeconds <= 0)
            {
                _logger.LogWarning("Das Schichtfenster ist leer oder negativ; es kann nicht geplant werden.");
                return null;
            }

            long vehicleCapacity = ScaleUnits(request.VehicleCapacityUnits, nameof(request.VehicleCapacityUnits));
            if (vehicleCapacity <= 0)
            {
                _logger.LogWarning("Das Fahrzeug hat keine nutzbare Kapazität; es kann nicht geplant werden.");
                return null;
            }

            var candidates = TrimCandidates(request.Candidates);
            if (candidates.Count == 0)
            {
                _logger.LogInformation("Keine Sammelpunkte zur Auswahl vorhanden.");
                return null;
            }

            var nodes = BuildNodes(request, candidates, vehicleCapacity);

            // Der Punktkatalog wird einmal aus allen Knoten gebildet und danach nie erneut
            // abgefragt. Deckungsgleiche Knoten – etwa die Kopien einer Abladestelle – teilen
            // denselben Eintrag, und ein späteres Aussortieren von Knoten lässt lediglich
            // unbenutzte Einträge zurück. Eine zweite Matrixabfrage ist damit nie erforderlich.
            var catalogue = BuildPointCatalogue(nodes);
            var matrix = await _roadNetwork
                .GetMatrixAsync(catalogue.Points, cancellationToken)
                .ConfigureAwait(false);

            var nodeToPoint = MapNodesToPoints(nodes, catalogue);

            // Punkte ohne Verbindung zum Betriebshof machen das Modell unlösbar und werden vorab
            // entfernt. Ohne diese Prüfung scheitert die gesamte Schichtplanung an einem einzigen
            // falsch erfassten Standort.
            var unreachable = FindUnreachableCandidates(nodes, nodeToPoint, matrix);
            if (unreachable.Count > 0)
            {
                _logger.LogWarning(
                    "{Count} Sammelpunkte sind über das Straßennetz nicht erreichbar und werden ausgelassen: {Ids}.",
                    unreachable.Count, string.Join(", ", unreachable));

                nodes = nodes
                    .Where(n => n.Kind != PlannedStopKind.Collection || !unreachable.Contains(n.Candidate!.Id))
                    .ToList();

                nodeToPoint = MapNodesToPoints(nodes, catalogue);
            }

            if (!nodes.Any(n => n.Kind == PlannedStopKind.Collection))
            {
                _logger.LogWarning("Nach der Erreichbarkeitsprüfung bleibt kein Sammelpunkt übrig.");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            LogRequiredLoads(request, candidates.Count);

            var plan = Solve(request, nodes, nodeToPoint, matrix, vehicleCapacity, shiftSeconds, unreachable);
            if (plan is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            return Refine(request, plan, nodes, catalogue, matrix, vehicleCapacity, shiftSeconds, unreachable)
                   ?? plan;
        }
        catch (RoutingEngineException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Die Sammeltourenplanung ist fehlgeschlagen.");
            throw new RoutingEngineException("Die Sammeltourenplanung ist fehlgeschlagen.", ex);
        }
    }

    // ==========================================================================================
    // Nachoptimierung
    // ==========================================================================================

    /// <summary>
    /// Zweiter Durchgang: Die im ersten Durchgang <em>ausgewählten</em> Sammelpunkte werden noch
    /// einmal optimiert, diesmal als feste Menge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Der erste Durchgang muss zwei Dinge gleichzeitig entscheiden: <b>welche</b> Punkte bedient
    /// werden und <b>in welcher Reihenfolge</b>. Bei mehreren hundert wählbaren Punkten verbraucht
    /// die Auswahlentscheidung einen großen Teil des Rechenzeitbudgets, sodass für die Reihenfolge
    /// zu wenig übrig bleibt.
    /// </para>
    /// <para>
    /// Steht die Menge dagegen fest, entfällt die Auswahlentscheidung vollständig und dasselbe
    /// Zeitbudget fließt allein in die Reihenfolge. Genau das erklärt, warum eine Neuberechnung
    /// derselben Route bisher regelmäßig ein besseres Ergebnis lieferte als die Erstberechnung:
    /// Bei der Neuberechnung ist die Punktmenge bereits festgelegt. Dieser Durchgang holt diesen
    /// Vorteil in die Erstberechnung.
    /// </para>
    /// <para>
    /// Das Ergebnis wird nur übernommen, wenn es messbar besser ist – Rückschritte sind
    /// ausgeschlossen. Die Matrix wird wiederverwendet, es entsteht also keine weitere Anfrage an
    /// das Straßennetz.
    /// </para>
    /// </remarks>
    /// <returns>Den verbesserten Plan, oder <c>null</c>, wenn keine Verbesserung erreichbar war.</returns>
    private CollectionPlan? Refine(
        CollectionPlanRequest request,
        CollectionPlan firstPass,
        List<PlanNode> nodes,
        PointCatalogue catalogue,
        RoadMatrix matrix,
        long vehicleCapacity,
        long shiftSeconds,
        IReadOnlyCollection<int> unreachable)
    {
        if (!_options.Planner.RefineSelection || firstPass.CollectedCandidateIds.Count == 0)
        {
            return null;
        }

        var selected = firstPass.CollectedCandidateIds.ToHashSet();

        var refinedNodes = nodes
            .Where(n => n.Kind != PlannedStopKind.Collection || selected.Contains(n.Candidate!.Id))
            .Select(n => n.Kind == PlannedStopKind.Collection
                ? n with { Candidate = n.Candidate! with { Mandatory = true } }
                : n)
            .ToList();

        var refinedMap = MapNodesToPoints(refinedNodes, catalogue);

        var secondPass = Solve(
            request, refinedNodes, refinedMap, matrix, vehicleCapacity, shiftSeconds, unreachable);

        if (secondPass is null)
        {
            return null;
        }

        // Eine Verbesserung darf niemals Sammelmenge kosten.
        if (secondPass.CollectedUnits + 0.001 < firstPass.CollectedUnits)
        {
            return null;
        }

        double capacityKilograms = request.VehicleCapacityUnits * _emissions.KilogramsPerCapacityUnit;
        double firstGrams = EstimateObjectiveGrams(firstPass, capacityKilograms);
        double secondGrams = EstimateObjectiveGrams(secondPass, capacityKilograms);

        if (secondGrams >= firstGrams)
        {
            _logger.LogInformation(
                "Nachoptimierung brachte keine Verbesserung ({First:F0} g gegenüber {Second:F0} g); "
                + "der erste Plan bleibt.",
                firstGrams, secondGrams);

            return null;
        }

        _logger.LogInformation(
            "Nachoptimierung verbesserte den Plan um {Percent:F1} % ({First:F0} g auf {Second:F0} g CO2e, "
            + "{FirstKm:F1} km auf {SecondKm:F1} km).",
            (firstGrams - secondGrams) / firstGrams * 100.0,
            firstGrams, secondGrams,
            firstPass.EstimatedMeters / 1000.0, secondPass.EstimatedMeters / 1000.0);

        return secondPass;
    }

    /// <summary>
    /// Bewertet einen Plan in derselben Größe, die auch die Zielfunktion minimiert, damit zwei
    /// Durchgänge vergleichbar sind.
    /// </summary>
    private double EstimateObjectiveGrams(CollectionPlan plan, double capacityKilograms)
    {
        double grams = _emissions.GramsForTravelAtAveragePayload(plan.EstimatedMeters, capacityKilograms);

        grams += plan.Stops.Count(s => s.Kind == PlannedStopKind.Collection) * _emissions.GramsPerStop;
        grams += plan.Stops.Count(s => s.Kind == PlannedStopKind.Disposal) * _emissions.GramsPerDisposalStop;

        return grams;
    }

    /// <summary>
    /// Protokolliert, wie viele Fahrzeugladungen die Sammelmenge mindestens erfordert.
    /// </summary>
    /// <remarks>
    /// Diese Angabe verhindert Fehldeutungen der Schrittliste: Ergibt die Menge etwa 41 Behälter zu
    /// je 1,2 Einheiten bei 25 Einheiten Nutzlast, passen höchstens 20 Behälter in eine Ladung und
    /// es sind zwingend drei Entleerungen nötig – die dritte für die restlichen 1,2 Einheiten. Ein
    /// solcher kleiner Rest-Halt sieht nach einem Planungsfehler aus, ist aber unvermeidbar.
    /// </remarks>
    private void LogRequiredLoads(CollectionPlanRequest request, int candidateCount)
    {
        double totalUnits = request.Candidates.Sum(c => Math.Max(0.0, c.Units));
        if (request.VehicleCapacityUnits <= 0.0 || totalUnits <= 0.0)
        {
            return;
        }

        int minimumLoads = (int)Math.Ceiling(totalUnits / request.VehicleCapacityUnits);

        _logger.LogInformation(
            "{Candidates} Sammelpunkte mit zusammen {Units:F1} Einheiten bei {Capacity:F1} Einheiten Nutzlast: "
            + "mindestens {Loads} Fahrzeugladungen erforderlich, wenn alle bedient werden.",
            candidateCount, totalUnits, request.VehicleCapacityUnits, minimumLoads);
    }

    // ==========================================================================================
    // Modellaufbau und Lösung
    // ==========================================================================================

    /// <summary>Baut das OR-Tools-Modell auf, löst es und übersetzt die Lösung zurück.</summary>
    private CollectionPlan? Solve(
        CollectionPlanRequest request,
        List<PlanNode> nodes,
        int[] nodeToPoint,
        RoadMatrix matrix,
        long vehicleCapacity,
        long shiftSeconds,
        IReadOnlyCollection<int> unreachable)
    {
        int nodeCount = nodes.Count;

        // Knoten 0 ist der Schichtstart, Knoten 1 das Schichtende. Beide sind keine besuchbaren
        // Aufgaben, sondern Anfang und Ende der einen Fahrzeugtour.
        using var manager = new RoutingIndexManager(nodeCount, 1, new[] { 0 }, new[] { 1 });
        using var model = new RoutingModel(manager);

        double capacityKilograms = request.VehicleCapacityUnits * _emissions.KilogramsPerCapacityUnit;

        var co2 = BuildCo2Matrix(nodes, nodeToPoint, matrix, capacityKilograms);
        var seconds = BuildTimeMatrix(nodes, nodeToPoint, matrix);
        var demands = BuildDemands(nodes, vehicleCapacity);

        // Die Delegaten müssen die gesamte Lösungsdauer überleben; sie werden deshalb in lokalen
        // Variablen gehalten und am Ende ausdrücklich am Leben erhalten.
        LongLongToLong co2Callback = (from, to) => co2[manager.IndexToNode(from)][manager.IndexToNode(to)];
        LongLongToLong timeCallback = (from, to) => seconds[manager.IndexToNode(from)][manager.IndexToNode(to)];
        LongToLong demandCallback = index => demands[manager.IndexToNode(index)];

        // ---- Zielfunktion: Emissionen, nicht Fahrzeit --------------------------------------
        int co2Index = model.RegisterTransitCallback(co2Callback);
        model.SetArcCostEvaluatorOfAllVehicles(co2Index);

        // ---- Nebenbedingung: Schichtdauer --------------------------------------------------
        // Die Obergrenze der Dimension begrenzt jeden Zeitpunkt der Tour, also auch die Rückkehr.
        // Unbefahrbare Verbindungen tragen einen sehr großen Zeitwert und werden dadurch zugleich
        // unzulässig, ohne dass die Kostenarithmetik überläuft.
        int timeIndex = model.RegisterTransitCallback(timeCallback);
        model.AddDimension(timeIndex, 0, shiftSeconds, true, TimeDimension);
        var time = model.GetDimensionOrDie(TimeDimension);

        // ---- Nebenbedingung: Ladung mit Zwischenentleerung ---------------------------------
        int demandIndex = model.RegisterUnaryTransitCallback(demandCallback);
        model.AddDimensionWithVehicleCapacity(
            demandIndex,
            vehicleCapacity,           // Spielraum, den ausschließlich Abladestellen nutzen dürfen
            new[] { vehicleCapacity },
            true,                      // Die Schicht beginnt mit leerem Fahrzeug.
            LoadDimension);

        var load = model.GetDimensionOrDie(LoadDimension);
        var solver = model.solver();

        ApplyLoadResetAtDisposalSites(model, manager, nodes, load, solver, vehicleCapacity);

        bool hasDisposalSite = nodes.Any(n => n.Kind == PlannedStopKind.Disposal);
        if (_options.Planner.RequireEmptyOnReturn && hasDisposalSite)
        {
            // Das Fahrzeug muss leer zurückkommen; damit ist die letzte Station zwingend eine
            // Abladestelle. Ohne verfügbare Abladestelle wäre die Bedingung unerfüllbar, deshalb
            // die Prüfung.
            load.CumulVar(model.End(0)).SetMax(0);
        }

        ApplyDisjunctions(request, model, manager, nodes);

        var searchParameters = BuildSearchParameters();

        // Das gelieferte Assignment gehört dem Modell und darf nicht eigenständig freigegeben
        // werden; es wird mit dem Modell zusammen verworfen.
        var solution = model.SolveWithParameters(searchParameters);

        string status = DescribeStatus(model.GetStatus());

        // Verhindert, dass der Garbage Collector die an OR-Tools übergebenen Delegaten einzieht,
        // während die native Suche noch läuft.
        GC.KeepAlive(co2Callback);
        GC.KeepAlive(timeCallback);
        GC.KeepAlive(demandCallback);

        if (solution is null)
        {
            _logger.LogWarning("Der Optimierer hat keine zulässige Lösung gefunden (Status {Status}).", status);
            return null;
        }

        return ExtractPlan(request, nodes, nodeToPoint, matrix, model, manager, solution, load, time, unreachable, status);
    }

    /// <summary>
    /// Setzt die Ladung an jeder Abladestelle exakt auf null und verbietet Ladungssprünge überall
    /// sonst.
    /// </summary>
    /// <remarks>
    /// Der Spielraum (<c>SlackVar</c>) einer Dimension ist eine freie Variable. Bliebe er
    /// unbeschränkt, könnte der Optimierer Ladung an beliebiger Stelle "verschwinden" lassen und
    /// die Kapazitätsrechnung wäre wertlos. Deshalb wird er überall auf null fixiert und nur an
    /// Abladestellen so gebunden, dass genau die vorhandene Ladung abgegeben wird.
    /// </remarks>
    private static void ApplyLoadResetAtDisposalSites(
        RoutingModel model,
        RoutingIndexManager manager,
        List<PlanNode> nodes,
        RoutingDimension load,
        Solver solver,
        long vehicleCapacity)
    {
        for (int node = 0; node < nodes.Count; node++)
        {
            long index = manager.NodeToIndex(node);
            if (index < 0 || model.IsEnd(index))
            {
                // Endknoten besitzen keinen Nachfolger und damit keinen Spielraum.
                continue;
            }

            if (nodes[node].Kind == PlannedStopKind.Disposal)
            {
                // Der Bedarf der Abladestelle ist -Kapazität. Mit diesem Spielraum ergibt sich für
                // den Nachfolger genau: Ladung - Kapazität + (Kapazität - Ladung) = 0.
                solver.Add(load.SlackVar(index) == vehicleCapacity - load.CumulVar(index));
            }
            else
            {
                load.SlackVar(index).SetValue(0);
            }
        }
    }

    /// <summary>
    /// Macht Sammelpunkte und Abladestellen zu optionalen Knoten und legt damit fest, wann sich das
    /// Anfahren lohnt.
    /// </summary>
    private void ApplyDisjunctions(
        CollectionPlanRequest request,
        RoutingModel model,
        RoutingIndexManager manager,
        List<PlanNode> nodes)
    {
        for (int node = 0; node < nodes.Count; node++)
        {
            var spec = nodes[node];
            long index = manager.NodeToIndex(node);
            if (index < 0)
            {
                continue;
            }

            switch (spec.Kind)
            {
                case PlannedStopKind.Collection:
                    model.AddDisjunction(new[] { index }, ComputeDropPenalty(request, spec.Candidate!));
                    break;

                case PlannedStopKind.Disposal:
                    // Eine Abladestelle wird nur angefahren, wenn die Kapazität es erzwingt.
                    // Straffreiheit heißt: das Auslassen kostet nichts.
                    model.AddDisjunction(new[] { index }, 0L);
                    break;

                default:
                    // Start und Ende sind keine besuchbaren Aufgaben.
                    break;
            }
        }
    }

    /// <summary>
    /// Berechnet die Straffunktion für das Auslassen eines Sammelpunkts – der eigentliche
    /// Effizienzregler des Verfahrens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bei <c>FillShift = false</c> ist die Strafe der in Gramm CO2e ausgedrückte Nutzen der
    /// Sammlung. Der Optimierer fährt einen Punkt genau dann an, wenn die dadurch zusätzlich
    /// entstehenden Emissionen kleiner sind als dieser Nutzen. Das ist ein echtes
    /// Grenzkosten-Kriterium und ersetzt die frühere Heuristik, die Punkte allein nach
    /// Priorität minus Luftlinie auswählte und die Route bis zur Schichtgrenze aufblähte.
    /// </para>
    /// <para>
    /// Bei <c>FillShift = true</c> überwiegt eine sehr große Strafe jede Fahrkostendifferenz. Der
    /// Optimierer minimiert dann zuerst die Anzahl ausgelassener Punkte und erst danach die
    /// Emissionen – also "so viel wie möglich sammeln, aber auf der emissionsgünstigsten Route".
    /// Die Prioritätsgewichtung entscheidet, welche Punkte entfallen, wenn die Schicht bindet.
    /// </para>
    /// </remarks>
    private long ComputeDropPenalty(CollectionPlanRequest request, CollectionCandidate candidate)
    {
        if (candidate.Mandatory)
        {
            // Ausdrücklich zugeordnete Punkte erhalten die höchste Strafe: sie werden nur dann
            // ausgelassen, wenn die Schicht sie nachweislich nicht mehr hergibt.
            return FillShiftDropPenalty;
        }

        int priority = Math.Clamp(candidate.Priority, 0, 100);

        double priorityFactor = Math.Max(
            _options.Planner.MinimumPriorityFactor,
            priority / 100.0);

        double penalty = request.FillShift
            ? FillShiftDropPenalty * priorityFactor
            : Math.Max(0.0, candidate.Units) * _options.Planner.CollectionValueGramsPerUnit * priorityFactor;

        // Auf den zulässigen Bereich begrenzen: Straffunktionen müssen nicht negativ sein und
        // dürfen die Kostensumme nicht zum Überlauf bringen.
        return (long)Math.Clamp(Math.Round(penalty), 0.0, FillShiftDropPenalty);
    }

    /// <summary>Stellt die Suchparameter des Optimierers zusammen.</summary>
    private RoutingSearchParameters BuildSearchParameters()
    {
        var parameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();

        // Günstigster Bogen als Startlösung, danach Guided Local Search: die für kapazitierte
        // Tourenprobleme bewährte Kombination.
        parameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
        parameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.GuidedLocalSearch;
        parameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration
        {
            Seconds = _options.Planner.SolverTimeSeconds
        };
        parameters.LogSearch = false;

        return parameters;
    }

    /// <summary>Liest die Lösung aus und übersetzt sie in das fachliche Ergebnis.</summary>
    private CollectionPlan ExtractPlan(
        CollectionPlanRequest request,
        List<PlanNode> nodes,
        int[] nodeToPoint,
        RoadMatrix matrix,
        RoutingModel model,
        RoutingIndexManager manager,
        Assignment solution,
        RoutingDimension load,
        RoutingDimension time,
        IReadOnlyCollection<int> unreachable,
        string status)
    {
        var stops = new List<PlannedStop>();
        var collected = new List<int>();
        var visitedCandidates = new HashSet<int>();

        int meters = 0;
        long travelSeconds = 0;
        long serviceSeconds = 0;
        int reloads = 0;

        long index = model.Start(0);
        int previousNode = -1;

        while (true)
        {
            int node = manager.IndexToNode(index);
            var spec = nodes[node];

            if (previousNode >= 0)
            {
                int fromPoint = nodeToPoint[previousNode];
                int toPoint = nodeToPoint[node];

                if (matrix.IsReachable(fromPoint, toPoint))
                {
                    meters += (int)matrix.Meters(fromPoint, toPoint);
                    travelSeconds += matrix.Seconds(fromPoint, toPoint);
                }
            }

            long serviceForStop = spec.ServiceSeconds;
            serviceSeconds += serviceForStop;

            stops.Add(new PlannedStop(
                spec.Kind,
                spec.ReferenceId,
                spec.Label,
                spec.Location,
                TimeSpan.FromSeconds(solution.Value(time.CumulVar(index))),
                TimeSpan.FromSeconds(serviceForStop),
                DescaleUnits(LoadAfter(solution, load, index, spec))));

            switch (spec.Kind)
            {
                case PlannedStopKind.Collection:
                    collected.Add(spec.Candidate!.Id);
                    visitedCandidates.Add(spec.Candidate.Id);
                    break;

                case PlannedStopKind.Disposal:
                    reloads++;
                    break;

                default:
                    break;
            }

            if (model.IsEnd(index))
            {
                break;
            }

            previousNode = node;
            index = solution.Value(model.NextVar(index));
        }

        var skipped = nodes
            .Where(n => n.Kind == PlannedStopKind.Collection && !visitedCandidates.Contains(n.Candidate!.Id))
            .Select(n => n.Candidate!.Id)
            .ToList();

        double collectedUnits = nodes
            .Where(n => n.Kind == PlannedStopKind.Collection && visitedCandidates.Contains(n.Candidate!.Id))
            .Sum(n => n.Candidate!.Units);

        _logger.LogInformation(
            "Sammelplan erstellt: {Collected} Punkte bedient, {Skipped} ausgelassen, {Unreachable} unerreichbar, "
            + "{Reloads} Entleerungen, {Kilometers:F1} km, Status {Status}, Modus {Mode}.",
            collected.Count, skipped.Count, unreachable.Count, reloads, meters / 1000.0, status,
            request.FillShift ? "Schicht füllen" : "Grenzkosten");

        return new CollectionPlan(
            stops,
            collected,
            skipped,
            unreachable.ToList(),
            meters,
            TimeSpan.FromSeconds(travelSeconds),
            TimeSpan.FromSeconds(serviceSeconds),
            collectedUnits,
            reloads,
            status);
    }

    /// <summary>Ermittelt die Ladung nach Abschluss eines Halts.</summary>
    private static long LoadAfter(Assignment solution, RoutingDimension load, long index, PlanNode spec)
    {
        long before = solution.Value(load.CumulVar(index));

        // An einer Abladestelle wird das Fahrzeug vollständig entleert.
        return spec.Kind == PlannedStopKind.Disposal ? 0L : before + spec.ScaledDemand;
    }

    // ==========================================================================================
    // Aufbau der Matrizen
    // ==========================================================================================

    /// <summary>
    /// Baut die Emissionsmatrix, die als Zielfunktion dient. Die Emission eines Halts wird dem
    /// eingehenden Bogen zugeschlagen, sodass jeder besuchte Halt genau einmal belastet wird.
    /// </summary>
    private long[][] BuildCo2Matrix(
        List<PlanNode> nodes,
        int[] nodeToPoint,
        RoadMatrix matrix,
        double capacityKilograms)
    {
        int count = nodes.Count;
        var co2 = new long[count][];

        for (int from = 0; from < count; from++)
        {
            co2[from] = new long[count];

            for (int to = 0; to < count; to++)
            {
                if (from == to)
                {
                    continue;
                }

                int fromPoint = nodeToPoint[from];
                int toPoint = nodeToPoint[to];

                if (!matrix.IsReachable(fromPoint, toPoint))
                {
                    co2[from][to] = RoadMatrix.Unreachable;
                    continue;
                }

                double grams = _emissions.GramsForTravelAtAveragePayload(
                    matrix.Meters(fromPoint, toPoint), capacityKilograms);

                // Eine Entleerung ist deutlich aufwendiger als ein Sammelhalt (Anmelden, Wiegen,
                // Kippen, Rangieren). Diese Unterscheidung ist der emissionsseitige Gegenspieler zu
                // unnötigen Zwischenentleerungen.
                grams += nodes[to].Kind switch
                {
                    PlannedStopKind.Collection => _emissions.GramsPerStop,
                    PlannedStopKind.Disposal => _emissions.GramsPerDisposalStop,
                    _ => 0.0
                };

                co2[from][to] = (long)Math.Clamp(Math.Round(grams), 0.0, RoadMatrix.Unreachable);
            }
        }

        return co2;
    }

    /// <summary>
    /// Baut die Zeitmatrix. Die Bedienzeit des Ausgangsknotens gehört zum Bogen, damit die
    /// Bedienzeit jedes besuchten Halts genau einmal in die Schichtdauer eingeht.
    /// </summary>
    private long[][] BuildTimeMatrix(List<PlanNode> nodes, int[] nodeToPoint, RoadMatrix matrix)
    {
        int count = nodes.Count;
        var seconds = new long[count][];

        for (int from = 0; from < count; from++)
        {
            seconds[from] = new long[count];
            long service = nodes[from].ServiceSeconds;

            for (int to = 0; to < count; to++)
            {
                if (from == to)
                {
                    continue;
                }

                int fromPoint = nodeToPoint[from];
                int toPoint = nodeToPoint[to];

                seconds[from][to] = matrix.IsReachable(fromPoint, toPoint)
                    ? matrix.Seconds(fromPoint, toPoint) + service
                    : RoadMatrix.Unreachable;
            }
        }

        return seconds;
    }

    /// <summary>Baut den Bedarfsvektor: Sammelpunkte laden auf, Abladestellen entladen vollständig.</summary>
    private static long[] BuildDemands(List<PlanNode> nodes, long vehicleCapacity)
    {
        var demands = new long[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            demands[i] = nodes[i].ScaledDemand;
        }

        return demands;
    }

    /// <summary>
    /// Bedienzeit eines Sammelpunkts in Sekunden.
    /// </summary>
    /// <remarks>
    /// Eine punktgenaue Angabe am Sammelpunkt hat Vorrang vor der globalen Vorgabe. Ohne
    /// realistische Bedienzeiten ist eine Schichtplanung wertlos: Die Vorversion übergab
    /// Sammelpunkte mit null Sekunden an den Optimierer, weshalb die Auswahl die Schicht
    /// ausschließlich mit Fahrzeit füllen konnte und die Touren massiv überdehnte.
    /// </remarks>
    private long CollectionServiceSeconds(CollectionCandidate candidate)
    {
        var times = _options.ServiceTimes;

        if (candidate.ServiceSecondsOverride is { } fixedSeconds)
        {
            return Math.Max(0L, fixedSeconds);
        }

        return times.ContainerBaseSeconds
               + (long)Math.Round(times.ContainerSecondsPerUnit * Math.Max(0.0, candidate.Units));
    }

    /// <summary>
    /// Bedienzeit einer Entleerung in Sekunden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setzt sich aus einem festen Anteil (Anmelden, Wiegen, Rangieren) und einem mengenabhängigen
    /// Anteil zusammen. Eine Angabe an der Abladestelle hat Vorrang vor der globalen Vorgabe.
    /// </para>
    /// <para>
    /// Der mengenabhängige Anteil wird mit einer <b>vollen</b> Fahrzeugladung gerechnet, weil
    /// Bogenkosten in einem Tourenoptimierer nicht vom aktuellen Ladezustand abhängen dürfen. Für
    /// eine letzte Teilentleerung überschätzt das die Zeit; die Planung ist damit betrieblich auf
    /// der sicheren Seite, und die berichtete Bedienzeit stimmt mit der geplanten Ankunftszeit
    /// überein.
    /// </para>
    /// </remarks>
    private long DisposalServiceSeconds(DisposalSite site, double vehicleCapacityUnits)
    {
        long flat = site.ServiceSecondsOverride is { } fixedSeconds
            ? Math.Max(0L, fixedSeconds)
            : _options.ServiceTimes.FacilitySeconds;

        double perUnit = Math.Max(0.0, site.ServiceSecondsPerUnit ?? 0.0);

        return flat + (long)Math.Round(perUnit * Math.Max(0.0, vehicleCapacityUnits));
    }

    // ==========================================================================================
    // Knotenaufbau und Hilfsfunktionen
    // ==========================================================================================

    /// <summary>
    /// Baut die Knotenliste: Start, Ende, Sammelpunkte und die vervielfachten Abladestellen.
    /// </summary>
    private List<PlanNode> BuildNodes(
        CollectionPlanRequest request,
        IReadOnlyList<CollectionCandidate> candidates,
        long vehicleCapacity)
    {
        var nodes = new List<PlanNode>(candidates.Count + 8)
        {
            new(PlannedStopKind.Start, request.ShiftStart, null, null, 0L, 0L),
            new(PlannedStopKind.End, request.ShiftEnd, null, null, 0L, 0L)
        };

        foreach (var candidate in candidates)
        {
            nodes.Add(new PlanNode(
                PlannedStopKind.Collection,
                candidate.Location,
                candidate,
                null,
                ScaleUnits(candidate.Units, nameof(candidate.Units)),
                CollectionServiceSeconds(candidate)));
        }

        int maxReloads = _options.Planner.MaxReloadsPerShift;
        if (maxReloads <= 0 || request.DisposalSites.Count == 0)
        {
            return nodes;
        }

        foreach (var site in request.DisposalSites)
        {
            // Jede Kopie ist ein möglicher Entleerungsvorgang an dieser Stelle. Die Aufnahmefähigkeit
            // der Abladestelle begrenzt, wie viele Fahrzeugladungen sie annehmen kann.
            int visits = maxReloads;
            if (site.MaximumUnits > 0.0 && request.VehicleCapacityUnits > 0.0)
            {
                int allowed = (int)Math.Floor(site.MaximumUnits / request.VehicleCapacityUnits);
                visits = Math.Clamp(allowed, 0, maxReloads);
            }

            long disposalService = DisposalServiceSeconds(site, request.VehicleCapacityUnits);

            for (int i = 0; i < visits; i++)
            {
                nodes.Add(new PlanNode(
                    PlannedStopKind.Disposal,
                    site.Location,
                    null,
                    site,
                    -vehicleCapacity,
                    disposalService));
            }
        }

        return nodes;
    }

    /// <summary>
    /// Begrenzt die Kandidatenmenge auf die konfigurierte Obergrenze, mit den höchsten Prioritäten
    /// zuerst. Schützt Valhalla und den Speicher vor übergroßen Matrizen.
    /// </summary>
    private List<CollectionCandidate> TrimCandidates(IReadOnlyList<CollectionCandidate> candidates)
    {
        int limit = _options.Planner.MaxContainersConsidered;
        if (candidates.Count <= limit)
        {
            return candidates.ToList();
        }

        _logger.LogInformation(
            "{Total} Sammelpunkte übersteigen die Obergrenze von {Limit}; es werden die höchstpriorisierten verwendet.",
            candidates.Count, limit);

        return candidates
            .OrderByDescending(c => c.Priority)
            .ThenByDescending(c => c.Units)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Ein Verzeichnis der eindeutigen Koordinaten aller Knoten.
    /// </summary>
    /// <param name="Points">Die eindeutigen Koordinaten; bestimmt die Matrixindizes.</param>
    /// <param name="Lookup">Zuordnung Koordinate auf Matrixindex.</param>
    /// <remarks>
    /// Kopien einer Abladestelle liegen auf derselben Koordinate und dürfen die Matrixabfrage nicht
    /// vervielfachen. Das Verzeichnis wird einmal aufgebaut und für alle Optimierungsdurchgänge
    /// wiederverwendet, sodass das Straßennetz nur ein einziges Mal befragt wird.
    /// </remarks>
    private sealed record PointCatalogue(List<Coordinate> Points, Dictionary<Coordinate, int> Lookup);

    /// <summary>Baut das Koordinatenverzeichnis aus allen Knoten.</summary>
    private static PointCatalogue BuildPointCatalogue(List<PlanNode> nodes)
    {
        var points = new List<Coordinate>(nodes.Count);
        var lookup = new Dictionary<Coordinate, int>(nodes.Count);

        foreach (var node in nodes)
        {
            if (lookup.ContainsKey(node.Location))
            {
                continue;
            }

            lookup[node.Location] = points.Count;
            points.Add(node.Location);
        }

        return new PointCatalogue(points, lookup);
    }

    /// <summary>
    /// Bildet eine – auch unvollständige – Knotenliste auf die Matrixindizes des Verzeichnisses ab.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wenn ein Knoten nicht im Verzeichnis steht.</exception>
    private static int[] MapNodesToPoints(List<PlanNode> nodes, PointCatalogue catalogue)
    {
        var nodeToPoint = new int[nodes.Count];

        for (int i = 0; i < nodes.Count; i++)
        {
            if (!catalogue.Lookup.TryGetValue(nodes[i].Location, out int pointIndex))
            {
                // Kann nur durch einen Programmierfehler auftreten: das Verzeichnis wird aus der
                // vollständigen Knotenliste gebildet.
                throw new InvalidOperationException(
                    "Ein Knoten ist nicht im Koordinatenverzeichnis enthalten.");
            }

            nodeToPoint[i] = pointIndex;
        }

        return nodeToPoint;
    }

    /// <summary>Ermittelt Sammelpunkte ohne befahrbare Verbindung zum Schichtstart.</summary>
    private static HashSet<int> FindUnreachableCandidates(
        List<PlanNode> nodes,
        int[] nodeToPoint,
        RoadMatrix matrix)
    {
        var unreachable = new HashSet<int>();
        int depotPoint = nodeToPoint[0];

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind != PlannedStopKind.Collection)
            {
                continue;
            }

            if (!matrix.IsConnectedTo(nodeToPoint[i], depotPoint))
            {
                unreachable.Add(nodes[i].Candidate!.Id);
            }
        }

        return unreachable;
    }

    /// <summary>Berechnet die planbare Schichtdauer in Sekunden.</summary>
    private long ComputeUsableShiftSeconds(CollectionPlanRequest request)
    {
        double total = (request.ShiftEndTime - request.ShiftStartTime).TotalSeconds;
        if (total <= 0.0)
        {
            return 0L;
        }

        // Rüstzeiten, Pausen und Puffer bleiben unverplant. Das ist eine bewusste
        // Betriebsreserve, keine Effizienzvorgabe.
        return (long)Math.Floor(total * _options.Planner.UsableShiftFraction);
    }

    /// <summary>
    /// Skaliert Kapazitätseinheiten auf Ganzzahlen und prüft dabei ausdrücklich auf Überlauf.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Wenn der Wert nicht darstellbar ist.</exception>
    private long ScaleUnits(double units, string parameterName)
    {
        if (double.IsNaN(units) || double.IsInfinity(units))
        {
            throw new ArgumentOutOfRangeException(parameterName, units, "Der Wert ist keine gültige Zahl.");
        }

        double scaled = Math.Round(Math.Max(0.0, units) * _unitScale, MidpointRounding.AwayFromZero);

        if (scaled > MaxScaledCapacity)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, units,
                $"Der Wert übersteigt die darstellbare Kapazität von {MaxScaledCapacity / _unitScale} Einheiten.");
        }

        return (long)scaled;
    }

    /// <summary>
    /// Rechnet skalierte Ganzzahlen zurück in echte Kapazitätseinheiten.
    /// </summary>
    /// <remarks>
    /// Es wird auf die konfigurierte Stellenzahl gerundet, weil aus der Division ganzer Zahlen sonst
    /// Werte wie <c>2,4000000000000004</c> entstehen können. Da die Skalierung genau dieser
    /// Stellenzahl entspricht, geht dabei keine Information verloren.
    /// </remarks>
    private double DescaleUnits(long scaled)
        => Math.Round(scaled / (double)_unitScale, _capacityDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Übersetzt den OR-Tools-Status in eine nachweisfähige Beschreibung.</summary>
    private static string DescribeStatus(int status) => status switch
    {
        0 => "NotSolved",
        1 => "Success",
        2 => "PartialSuccessLocalOptimumNotReached",
        3 => "Fail",
        4 => "FailTimeout",
        5 => "Invalid",
        6 => "Infeasible",
        _ => $"Unknown({status})"
    };

    /// <summary>Ein Knoten des Optimierungsmodells.</summary>
    /// <param name="Kind">Art des Knotens.</param>
    /// <param name="Location">Position.</param>
    /// <param name="Candidate">Der Sammelpunkt, falls es sich um einen handelt.</param>
    /// <param name="Site">Die Abladestelle, falls es sich um eine handelt.</param>
    /// <param name="ScaledDemand">
    /// Ladungsänderung in skalierten Einheiten: positiv beim Sammeln, negativ beim Entleeren.
    /// </param>
    /// <param name="ServiceSeconds">
    /// Bereits aufgelöste Bedienzeit dieses Halts in Sekunden – punktgenaue Angabe oder globale
    /// Vorgabe. Beim Knotenaufbau einmal berechnet, damit Zeitmatrix und ausgegebene Schrittliste
    /// zwangsläufig denselben Wert verwenden.
    /// </param>
    private sealed record PlanNode(
        PlannedStopKind Kind,
        Coordinate Location,
        CollectionCandidate? Candidate,
        DisposalSite? Site,
        long ScaledDemand,
        long ServiceSeconds)
    {
        /// <summary>Fachliche Kennung des Halts, sofern vorhanden.</summary>
        public int? ReferenceId => Candidate?.Id ?? Site?.Id;

        /// <summary>Anzeigename des Halts.</summary>
        public string? Label => Candidate?.Label ?? Site?.Label;
    }
}
