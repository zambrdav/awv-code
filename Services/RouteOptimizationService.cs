using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Services.Routing;
using RouteEntity = SmartRoutingAPI.Models.Routes.Route;

namespace SmartRoutingAPI.Services;

/// <summary>
/// Orchestriert die Routenoptimierung: Auswahl der in Frage kommenden Sammelpunkte, Aufruf des
/// Planers, Ermittlung der tatsächlich gefahrenen Geometrie, Emissionsbilanz und Persistenz.
/// </summary>
/// <remarks>
/// <para>
/// Diese Klasse ersetzt die frühere Umsetzung vollständig. Entfallen sind insbesondere:
/// </para>
/// <list type="bullet">
///   <item><description>
///     die heuristische Kandidatenauswahl, die eine Route so lange vergrößerte, bis 80–90 % der
///     Schicht verbraucht waren – das war die Hauptursache überdehnter Touren;
///   </description></item>
///   <item><description>
///     die Schleife über bis zu 50 nacheinander gelöste Teilabschnitte, die jeweils nur lokal
///     optimal waren und dem Verfahren jede Gesamtsicht nahmen;
///   </description></item>
///   <item><description>
///     das Aneinanderhängen kodierter Polylines, das die Geometrie jeder mehrteiligen Route
///     zerstörte;
///   </description></item>
///   <item><description>
///     das Anhängen unverfolgter (<c>AsNoTracking</c>) Entitäten an eine verfolgte Navigation,
///     wodurch bei jeder Routenerstellung doppelte Sammelpunkt-Datensätze entstehen konnten.
///   </description></item>
/// </list>
/// <para>
/// Die Reihenfolge der Halte bestimmt der Planer anhand von Emissionen; die berichteten Kilometer
/// und die Geometrie stammen anschließend aus demselben Kostenmodell, sodass Planung und Nachweis
/// nicht auseinanderlaufen.
/// </para>
/// </remarks>
public sealed class RouteOptimizationService
{
    private readonly RoutingDBContext _context;
    private readonly ICollectionPlanner _planner;
    private readonly IRoadNetworkService _roadNetwork;
    private readonly IEmissionModel _emissions;
    private readonly ILogger<RouteOptimizationService> _logger;

    /// <summary>Erzeugt den Dienst.</summary>
    /// <param name="context">Der Datenbankkontext (scoped).</param>
    /// <param name="planner">Der Sammeltourenplaner.</param>
    /// <param name="roadNetwork">Zugang zum Straßennetz für Geometrie und Kennzahlen.</param>
    /// <param name="emissions">Das CO2e-Modell.</param>
    /// <param name="logger">Logger für Diagnose und Nachweis.</param>
    public RouteOptimizationService(
        RoutingDBContext context,
        ICollectionPlanner planner,
        IRoadNetworkService roadNetwork,
        IEmissionModel emissions,
        ILogger<RouteOptimizationService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _roadNetwork = roadNetwork ?? throw new ArgumentNullException(nameof(roadNetwork));
        _emissions = emissions ?? throw new ArgumentNullException(nameof(emissions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // -----------------------------------------------------------------------------------------
    // Öffentliche Einstiegspunkte
    // -----------------------------------------------------------------------------------------

    /// <summary>Erstellt, optimiert und speichert eine neue Route aus einem <see cref="RouteDTO"/>.</summary>
    /// <param name="route">Die Vorgaben der Route.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Die gespeicherte Route, oder <c>null</c>, wenn kein zulässiger Plan existiert.</returns>
    /// <exception cref="ArgumentException">Wenn Fahrzeug oder Skills ungültig sind.</exception>
    public async Task<RouteEntity?> CreateRouteInternalAsync(RouteDTO route, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(route);

            var vehicle = await _context.Vehicles.Include(v => v.Skills)
                .FirstOrDefaultAsync(v => v.Id == route.VehicleId, ct)
                .ConfigureAwait(false);

            var skills = await _context.Skills
                .Where(s => route.SkillsIdList.Contains(s.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (vehicle is null || skills.Count != route.SkillsIdList.Distinct().Count())
            {
                throw new ArgumentException("Invalid vehicle or skill ID");
            }

            var entity = new RouteEntity
            {
                Color = route.Color,
                Description = route.Description,
                Name = route.Name,
                EndLatitude = route.EndLatitude,
                EndLongitude = route.EndLongitude,
                ExternalReference = route.ExternalReference,
                Skills = skills,
                StartDate = route.StartDate,
                EndDate = route.EndDate,
                StartLatitude = route.StartLatitude,
                StartLongitude = route.StartLongitude,
                Vehicle = vehicle,
                FillShift = route.FillShift
            };

            var candidates = await LoadEligibleJobsAsync(entity, ct).ConfigureAwait(false);

            // Die Entität muss vor dem Zuweisen der Sammelpunkte bekannt sein, damit der
            // Änderungsverfolger die Zuordnung als Beziehung und nicht als Neuanlage erkennt.
            _context.Routes.Add(entity);

            bool planned = await ApplyPlanAsync(entity, candidates, mandatory: false, ct).ConfigureAwait(false);
            if (!planned)
            {
                // Kein zulässiger Plan: nichts halb Fertiges speichern.
                _context.Routes.Remove(entity);
                return null;
            }

            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
            return entity;
        }
        catch (ArgumentException)
        {
            throw;
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
            _logger.LogError(ex, "Die Routenerstellung ist fehlgeschlagen.");
            throw;
        }
    }

    /// <summary>
    /// Berechnet eine Route nach dem Hinzufügen bzw. Entfernen von Sammelpunkten neu und speichert
    /// sie optional (andernfalls Vorschau).
    /// </summary>
    /// <param name="routeId">Die Route.</param>
    /// <param name="addJobIds">Hinzuzufügende Sammelpunkte.</param>
    /// <param name="removeJobIds">Zu entfernende Sammelpunkte.</param>
    /// <param name="saveToDb">Ob das Ergebnis gespeichert werden soll.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Die neu berechnete Route, oder <c>null</c>, wenn sie nicht existiert oder kein Plan möglich ist.</returns>
    /// <exception cref="ArgumentException">Wenn Sammelpunkt-IDs unbekannt sind.</exception>
    public async Task<RouteEntity?> CalculateRouteWithJobChangesAsync(
        int routeId,
        IReadOnlyList<int> addJobIds,
        IReadOnlyList<int> removeJobIds,
        bool saveToDb,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(addJobIds);
            ArgumentNullException.ThrowIfNull(removeJobIds);

            var existing = await LoadRouteForRecalculationAsync(routeId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return null;
            }

            var wantedIds = existing.Jobs?.Select(j => j.Id).ToHashSet() ?? new HashSet<int>();
            foreach (int id in removeJobIds)
            {
                wantedIds.Remove(id);
            }

            foreach (int id in addJobIds)
            {
                wantedIds.Add(id);
            }

            var idList = wantedIds.ToList();
            var jobs = await _context.Jobs
                .Include(j => j.Skill)
                .Where(j => idList.Contains(j.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (jobs.Count != wantedIds.Count)
            {
                var found = jobs.Select(j => j.Id).ToHashSet();
                var missing = wantedIds.Where(id => !found.Contains(id)).ToList();
                throw new ArgumentException($"Invalid job IDs: {string.Join(", ", missing)}");
            }

            // Vom Disponenten ausdrücklich zugeordnete Punkte gelten als verbindlich: der Planer
            // versucht sie in jedem Fall aufzunehmen und meldet, was nicht in die Schicht passt.
            bool planned = await ApplyPlanAsync(existing, jobs, mandatory: true, ct).ConfigureAwait(false);
            if (!planned)
            {
                return null;
            }

            if (saveToDb)
            {
                await _context.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return existing;
        }
        catch (ArgumentException)
        {
            throw;
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
            _logger.LogError(ex, "Die Neuberechnung der Route {RouteId} ist fehlgeschlagen.", routeId);
            throw;
        }
    }

    /// <summary>Aktualisiert die Stammdaten einer Route und berechnet sie neu.</summary>
    /// <param name="id">Die Route.</param>
    /// <param name="metadata">Die neuen Stammdaten.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Die aktualisierte Route, oder <c>null</c>, wenn sie nicht existiert oder kein Plan möglich ist.</returns>
    /// <exception cref="ArgumentException">Wenn Fahrzeug oder Skills ungültig sind.</exception>
    public async Task<RouteEntity?> UpdateRouteMetadataAsync(
        int id,
        RouteMetadataDTO metadata,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(metadata);

            var existing = await LoadRouteForRecalculationAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return null;
            }

            var vehicle = await _context.Vehicles.Include(v => v.Skills)
                .FirstOrDefaultAsync(v => v.Id == metadata.VehicleId, ct)
                .ConfigureAwait(false);

            var skills = await _context.Skills
                .Where(s => metadata.SkillsIdList.Contains(s.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (vehicle is null || skills.Count != metadata.SkillsIdList.Distinct().Count())
            {
                throw new ArgumentException("Invalid vehicle or skill ID");
            }

            existing.Name = metadata.Name;
            existing.Description = metadata.Description;
            existing.StartDate = metadata.StartDate;
            existing.EndDate = metadata.EndDate;
            existing.StartLatitude = metadata.StartLatitude;
            existing.StartLongitude = metadata.StartLongitude;
            existing.EndLatitude = metadata.EndLatitude;
            existing.EndLongitude = metadata.EndLongitude;
            existing.Vehicle = vehicle;
            existing.FillShift = metadata.FillShift;

            existing.Skills.Clear();
            foreach (var skill in skills)
            {
                existing.Skills.Add(skill);
            }

            // Bei geänderten Stammdaten (Fahrzeug, Skills, Schichtfenster) ist die bisherige
            // Punktzuordnung nicht mehr zwangsläufig gültig; es wird daher frei neu geplant.
            var candidates = await LoadEligibleJobsAsync(existing, ct).ConfigureAwait(false);

            bool planned = await ApplyPlanAsync(existing, candidates, mandatory: false, ct).ConfigureAwait(false);
            if (!planned)
            {
                return null;
            }

            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }
        catch (ArgumentException)
        {
            throw;
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
            _logger.LogError(ex, "Die Aktualisierung der Route {RouteId} ist fehlgeschlagen.", id);
            throw;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Planung und Übernahme des Ergebnisses
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Plant die Route über die übergebenen Kandidaten und überträgt das Ergebnis in die Entität.
    /// </summary>
    /// <param name="route">Die zu planende Route (wird verändert).</param>
    /// <param name="candidateJobs">
    /// Die in Frage kommenden Sammelpunkte. Es müssen <b>verfolgte</b> Entitäten sein, damit die
    /// Zuordnung als Beziehung und nicht als Neuanlage gespeichert wird.
    /// </param>
    /// <param name="mandatory">Ob die Kandidaten als verbindlich zugeordnet gelten.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns><c>true</c>, wenn ein Plan übernommen wurde.</returns>
    private async Task<bool> ApplyPlanAsync(
        RouteEntity route,
        IReadOnlyList<Job> candidateJobs,
        bool mandatory,
        CancellationToken ct)
    {
        if (candidateJobs.Count == 0)
        {
            _logger.LogWarning(
                "Für Route \"{RouteName}\" stehen keine passenden Sammelpunkte zur Verfügung.", route.Name);
            return false;
        }

        var disposalSites = await LoadDisposalSitesAsync(route, ct).ConfigureAwait(false);
        if (disposalSites.Count == 0)
        {
            _logger.LogWarning(
                "Für Route \"{RouteName}\" ist keine passende Abladestelle definiert; "
                + "es kann nur eine einzige Fahrzeugladung geplant werden.", route.Name);
        }

        var jobsById = candidateJobs.ToDictionary(j => j.Id);

        var request = new CollectionPlanRequest
        {
            ShiftStart = new Coordinate(route.StartLatitude, route.StartLongitude),
            ShiftEnd = new Coordinate(route.EndLatitude, route.EndLongitude),
            ShiftStartTime = route.StartDate,
            ShiftEndTime = route.EndDate,
            VehicleCapacityUnits = route.Vehicle.Capacity,
            FillShift = route.FillShift,
            Candidates = candidateJobs
                .Select(j => new CollectionCandidate(
                    j.Id,
                    new Coordinate(j.Latitude, j.Longitude),
                    j.Capacity,
                    j.Priority,
                    string.IsNullOrWhiteSpace(j.Name) ? j.Description : j.Name,
                    mandatory,

                    // Punktgenaue Bedienzeit hat Vorrang vor der globalen Vorgabe.
                    j.ServiceTimeSeconds))
                .ToList(),
            DisposalSites = disposalSites
        };

        var plan = await _planner.PlanAsync(request, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return false;
        }

        // Die tatsächlich gefahrene Strecke und die Geometrie kommen aus demselben Kostenmodell wie
        // die Matrix. Damit stimmen geplante und berichtete Kilometer überein – anders als bisher,
        // wo die Geometrie aus aneinandergehängten Teil-Polylines bestand.
        var path = await _roadNetwork
            .GetPathAsync(plan.Stops.Select(s => s.Location).ToList(), ct)
            .ConfigureAwait(false);

        var emissions = ComputeEmissions(plan, path);

        route.Geometry = string.IsNullOrEmpty(path.EncodedGeometry) ? null : path.EncodedGeometry;
        route.Meters = path.Meters > 0 ? path.Meters : plan.EstimatedMeters;
        route.Duration = path.Legs.Count > 0 ? path.Duration : plan.TravelDuration;
        route.Service = plan.ServiceDuration;
        route.Co2eGrams = emissions;
        route.CollectedUnits = plan.CollectedUnits;
        route.EmissionFactorSource = _emissions.FactorSource;

        // Die Reihenfolge wird nicht mehr zusätzlich als eigenes Feld gespeichert. Sie steht
        // ausschließlich in der Schrittliste und wird für die Antwort daraus abgeleitet; damit
        // kann Reihenfolge und Schrittfolge nicht mehr auseinanderlaufen.
        ReplaceJobs(route, plan.CollectedCandidateIds, jobsById);
        ReplaceSteps(route, plan, path);

        if (plan.SkippedCandidateIds.Count > 0)
        {
            _logger.LogInformation(
                "Route \"{RouteName}\": {Count} Sammelpunkte wurden nicht aufgenommen "
                + "(Nutzen unter den zusätzlichen Emissionen oder Schichtgrenze erreicht): {Ids}.",
                route.Name, plan.SkippedCandidateIds.Count, string.Join(", ", plan.SkippedCandidateIds));
        }

        return true;
    }

    /// <summary>
    /// Berechnet die CO2e-Bilanz mit dem <em>tatsächlichen</em> Lastverlauf der Lösung.
    /// </summary>
    /// <remarks>
    /// Optimiert wird mit einer mittleren Zuladung, weil Bogenkosten zustandsunabhängig sein
    /// müssen. Berichtet wird dagegen abschnittsweise mit der wirklich transportierten Ladung.
    /// </remarks>
    private double ComputeEmissions(CollectionPlan plan, RoadPath path)
    {
        double kilogramsPerUnit = _emissions.KilogramsPerCapacityUnit;

        // Sammelhalt und Entleerung sind unterschiedlich aufwendig und werden getrennt bewertet.
        double grams =
            (plan.Stops.Count(s => s.Kind == PlannedStopKind.Collection) * _emissions.GramsPerStop)
            + (plan.Stops.Count(s => s.Kind == PlannedStopKind.Disposal) * _emissions.GramsPerDisposalStop);

        if (path.Legs.Count == plan.Stops.Count - 1)
        {
            for (int i = 0; i < path.Legs.Count; i++)
            {
                double payloadKilograms = plan.Stops[i].LoadAfterUnits * kilogramsPerUnit;
                grams += _emissions.GramsForTravel(path.Legs[i].Meters, payloadKilograms);
            }

            return grams;
        }

        // Rückfallebene: Passt die Abschnittszahl nicht zur Haltezahl, wird mit der mittleren Last
        // gerechnet. Das ist weniger genau, aber niemals stillschweigend falsch.
        _logger.LogWarning(
            "Die Zahl der Streckenabschnitte ({Legs}) passt nicht zur Zahl der Halte ({Stops}); "
            + "die Emissionsbilanz wird mit mittlerer Zuladung gerechnet.",
            path.Legs.Count, plan.Stops.Count);

        double averagePayload = plan.Stops.Count > 0
            ? plan.Stops.Average(s => s.LoadAfterUnits) * kilogramsPerUnit
            : 0.0;

        int meters = path.Meters > 0 ? path.Meters : plan.EstimatedMeters;
        return grams + _emissions.GramsForTravel(meters, averagePayload);
    }

    /// <summary>
    /// Ersetzt die Sammelpunkt-Zuordnung der Route durch <b>verfolgte</b> Entitäten.
    /// </summary>
    /// <remarks>
    /// Genau hier entstand in der Vorversion stille Datenkorruption: Es wurden Instanzen aus einer
    /// <c>AsNoTracking</c>-Abfrage zugewiesen, die der Änderungsverfolger als neu ansah und beim
    /// Speichern als zusätzliche Sammelpunkte einfügte.
    /// </remarks>
    private static void ReplaceJobs(
        RouteEntity route,
        IReadOnlyList<int> collectedIds,
        IReadOnlyDictionary<int, Job> jobsById)
    {
        var assigned = new List<Job>(collectedIds.Count);
        foreach (int id in collectedIds)
        {
            if (jobsById.TryGetValue(id, out var job))
            {
                assigned.Add(job);
            }
        }

        if (route.Jobs is null)
        {
            route.Jobs = assigned;
            return;
        }

        route.Jobs.Clear();
        foreach (var job in assigned)
        {
            route.Jobs.Add(job);
        }
    }

    /// <summary>Baut die Schrittliste der Route aus dem Plan und der gefahrenen Strecke neu auf.</summary>
    /// <remarks>
    /// Die Schrittliste ist die maßgebliche Darstellung des Fahrauftrags. Sie trägt daher eine
    /// ausdrückliche Reihenfolge (<see cref="Step.Sequence"/>) und einen fachlichen Verweis auf den
    /// Stammdatensatz jedes Halts (<see cref="Step.JobId"/> bzw.
    /// <see cref="Step.DeliveryDestinationId"/>). Zuvor ergab sich die Reihenfolge nur aus der
    /// Einfügereihenfolge, und der Halt verwies über eine <c>VroomId</c> auf die Kennung eines
    /// Optimierers, den es nicht mehr gibt.
    /// </remarks>
    private static void ReplaceSteps(RouteEntity route, CollectionPlan plan, RoadPath path)
    {
        var steps = new List<Step>(plan.Stops.Count);

        int cumulativeMeters = 0;
        var cumulativeTravel = TimeSpan.Zero;
        bool haveLegs = path.Legs.Count == plan.Stops.Count - 1;

        for (int i = 0; i < plan.Stops.Count; i++)
        {
            var stop = plan.Stops[i];

            if (i > 0 && haveLegs)
            {
                cumulativeMeters += path.Legs[i - 1].Meters;
                cumulativeTravel += path.Legs[i - 1].Duration;
            }

            var stepType = ToStepType(stop.Kind);

            steps.Add(new Step
            {
                Sequence = i,
                StepType = stepType,

                // Der Verweis wird nur für die Halteart gesetzt, für die er fachlich gilt.
                JobId = stepType == StepType.Collection ? stop.ReferenceId : null,
                DeliveryDestinationId = stepType == StepType.Disposal ? stop.ReferenceId : null,

                Arrival = route.StartDate + stop.ArrivalOffset,
                TravelTimeFromStart = cumulativeTravel,
                ServiceTime = stop.Service,
                DistanceFromStartMeters = cumulativeMeters,
                Description = stop.Label,
                Latitude = stop.Location.Latitude,
                Longitude = stop.Location.Longitude,

                // Die Ladung wird in echten Kapazitätseinheiten und mit voller Genauigkeit
                // gespeichert – also 1,2 und nicht 1 und nicht 1200.
                //
                // Frühere Varianten verloren Information: Ein ganzzahliger Wert schnitt Bruchteile
                // ab (aus 1,2 je Behälter wurde 1, nach fünf Behältern fehlte eine ganze Einheit),
                // und der um 1000 vergrößerte Wert erzwang eine Division im Client, die die
                // Einheit der Zahl von ihrer Bedeutung trennte. Die Skalierung, die ein
                // ganzzahliger Optimierer benötigt, bleibt vollständig in
                // OrToolsCollectionPlanner und erreicht die Schnittstelle nicht.
                LoadUnitsAfter = stop.LoadAfterUnits
            });
        }

        if (route.Steps is null)
        {
            route.Steps = steps;
            return;
        }

        route.Steps.Clear();
        foreach (var step in steps)
        {
            route.Steps.Add(step);
        }
    }

    /// <summary>Übersetzt die Halteart des Planers in den persistierten Schritttyp.</summary>
    /// <remarks>
    /// Beide Aufzählungen sind deckungsgleich definiert. Die Abbildung bleibt trotzdem
    /// ausdrücklich, damit eine künftige Erweiterung einer der beiden Seiten hier einen
    /// Übersetzungsfehler erzwingt, statt stillschweigend einen falschen Wert zu speichern.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Bei einer unbekannten Halteart.</exception>
    private static StepType ToStepType(PlannedStopKind kind) => kind switch
    {
        PlannedStopKind.Start => StepType.Start,
        PlannedStopKind.Collection => StepType.Collection,
        PlannedStopKind.Disposal => StepType.Disposal,
        PlannedStopKind.End => StepType.End,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unbekannte Halteart.")
    };

    // -----------------------------------------------------------------------------------------
    // Datenbeschaffung
    // -----------------------------------------------------------------------------------------

    /// <summary>Lädt eine Route mit allem, was für eine Neuberechnung nötig ist.</summary>
    private Task<RouteEntity?> LoadRouteForRecalculationAsync(int routeId, CancellationToken ct)
        => _context.Routes
            .AsSplitQuery()
            .Include(r => r.Jobs)!.ThenInclude(j => j.Skill)
            .Include(r => r.Skills)
            .Include(r => r.Vehicle)!.ThenInclude(v => v.Skills)
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == routeId, ct);

    /// <summary>
    /// Ermittelt die für diese Route in Frage kommenden Sammelpunkte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zwei Filter greifen: die Fähigkeiten (der Punkt muss von Route <em>und</em> Fahrzeug bedient
    /// werden können) und die Doppelbelegung.
    /// </para>
    /// <para>
    /// Der zweite Filter behebt einen bisher unwirksamen Mechanismus: die frühere Prioritätslogik
    /// sollte bereits eingeplante Punkte aussortieren, prüfte dazu aber eine Navigation, die nie
    /// geladen wurde. Sie war damit toter Code, und derselbe Container konnte in mehreren
    /// gleichzeitigen Touren stehen. Die Prüfung findet nun in der Datenbank statt.
    /// </para>
    /// </remarks>
    private async Task<List<Job>> LoadEligibleJobsAsync(RouteEntity route, CancellationToken ct)
    {
        var vehicleSkillIds = route.Vehicle.Skills?.Select(s => s.Id).ToHashSet() ?? new HashSet<int>();
        var usableSkillIds = route.Skills
            .Where(s => vehicleSkillIds.Contains(s.Id))
            .Select(s => s.Id)
            .Distinct()
            .ToList();

        if (usableSkillIds.Count == 0)
        {
            _logger.LogWarning(
                "Route \"{RouteName}\": Fahrzeug und Route haben keine gemeinsame Fähigkeit.", route.Name);
            return new List<Job>();
        }

        var overlappingJobIds = await _context.Routes
            .Where(r => r.Id != route.Id && r.StartDate < route.EndDate && r.EndDate > route.StartDate)
            .SelectMany(r => r.Jobs!)
            .Select(j => j.Id)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var blocked = overlappingJobIds.ToHashSet();

        // Bewusst ohne AsNoTracking: die Instanzen werden anschließend der Route zugeordnet und
        // müssen dem Änderungsverfolger bekannt sein.
        var eligible = await _context.Jobs
            .Include(j => j.Skill)
            .Where(j => j.Skill != null && usableSkillIds.Contains(j.Skill.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var candidates = eligible.Where(j => !blocked.Contains(j.Id)).ToList();

        _logger.LogInformation(
            "Route \"{RouteName}\": {Candidates} Sammelpunkte kommen in Frage "
            + "({Blocked} sind bereits in einer zeitlich überlappenden Tour eingeplant).",
            route.Name, candidates.Count, eligible.Count - candidates.Count);

        return candidates;
    }

    /// <summary>
    /// Lädt die Abladestellen, die zu den Fähigkeiten dieser Route passen.
    /// </summary>
    /// <remarks>
    /// Abladestellen ohne hinterlegte Fähigkeit gelten als universell nutzbar. Welche der
    /// passenden Stellen wann angefahren wird, entscheidet der Optimierer – das ist der Kern der
    /// Neuerung, denn Alternativstandorte je Ladung waren im vorherigen Verfahren nicht
    /// abbildbar.
    /// </remarks>
    private async Task<List<DisposalSite>> LoadDisposalSitesAsync(RouteEntity route, CancellationToken ct)
    {
        var routeSkillIds = route.Skills.Select(s => s.Id).ToHashSet();

        var sites = await _context.DeliveryDestinations
            .Include(d => d.Skills)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sites
            .Where(d => d.Skills.Count == 0 || d.Skills.Any(s => routeSkillIds.Contains(s.Id)))
            .Select(d => new DisposalSite(
                d.Id,
                new Coordinate(d.Latitude, d.Longitude),

                // Negative Werte bedeuten laut Datenmodell "unbegrenzt".
                d.MaximumCapacity < 0 ? 0.0 : d.MaximumCapacity,
                string.IsNullOrWhiteSpace(d.Name) ? d.Description : d.Name,

                // Bedienzeit der Abladestelle: der maßgebliche Regler dafür, wie oft der
                // Optimierer zwischenentleert.
                d.ServiceTimeSeconds,
                d.ServiceSecondsPerUnit))
            .ToList();
    }

}
