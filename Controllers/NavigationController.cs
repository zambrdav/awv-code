using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Misc;
using SmartRoutingAPI.Models.Navigation;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Services;
using SmartRoutingAPI.Services.Navigation;
using SmartRoutingAPI.Services.Routing;

namespace SmartRoutingAPI.Controllers;

/// <summary>
/// Live-Navigation für das Fahrzeug: Fortschritt gegen die geplante Route, Erkennung einer
/// Abweichung und Abbiegehinweise zum nächsten Halt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diese Endpunkte schließen die Lücke zwischen Planung und Fahrzeug.</b> Bis dahin lieferte die
/// API eine fertige Tour (Reihenfolge, Halte, eine zusammenhängende Polyline) und einen Abgleich
/// aufgezeichneter GPS-Spuren gegen das Straßennetz. Beides beantwortet nicht die Fragen, die im
/// Fahrerhaus entstehen: <em>Bin ich noch auf der Route? Wie weit ist es noch zu Halt 3? Wo muss ich
/// abbiegen? Und was gilt, wenn ich zur Pause fahre oder eine Straße gesperrt ist?</em>
/// </para>
/// <para>
/// <b>Arbeitsteilung.</b> Die API ist zustandslos und liefert Messungen; der Fahrer-Client führt den
/// Zustandsautomaten. Das ist keine Bequemlichkeit: Ein serverseitiger Zustand je Fahrzeug wäre bei
/// mehreren API-Instanzen sofort falsch — derselbe Mangel, der beim Auftragsstatus der Optimierung
/// bereits dokumentiert ist.
/// </para>
/// <para>
/// <b>Empfohlener Ablauf im Fahrer-Client.</b>
/// </para>
/// <list type="number">
///   <item><description>
///     Einmal je Schicht <c>GET routes/{id}/plan</c> abrufen. Die Antwort enthält die geplante
///     Geometrie und je Halt seine Lage darauf.
///   </description></item>
///   <item><description>
///     Bei jeder Ortung <b>örtlich</b> auf die Geometrie projizieren (Lotfußpunkt, Querabstand).
///     Kein Serveraufruf.
///   </description></item>
///   <item><description>
///     Überschreitet der Querabstand die gemeldete Toleranz in
///     <c>confirmationFixesRecommended</c> aufeinanderfolgenden Ortungen, <c>POST
///     routes/{id}/guidance</c> mit Position, Fahrtrichtung und angefahrenem Halt aufrufen. Das
///     Ergebnis enthält die neuen Abbiegehinweise ab der tatsächlichen Position.
///   </description></item>
///   <item><description>
///     <c>POST routes/{id}/progress</c> als maßgebliche Gegenprobe verwenden — bei Ankunft an einem
///     Halt, bei erkannter Abweichung oder in einem groben Zeittakt. <b>Nicht im Sekundentakt:</b>
///     Das wäre gegenüber der Ratenbegrenzung nicht durchhaltbar und ist wegen Schritt 2 nicht nötig.
///   </description></item>
/// </list>
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewRoutes)]
public class NavigationController : ControllerBase
{
    private readonly RoutingDBContext _context;
    private readonly INavigationService _navigation;
    private readonly RouteProgressCalculator _progress;
    private readonly NavigationOptions _options;
    private readonly ILogger<NavigationController> _logger;

    /// <summary>Erzeugt den Controller.</summary>
    /// <param name="context">Der Datenbankzugriff für gespeicherte Routen.</param>
    /// <param name="navigation">Der Dienst für Abbiegehinweise.</param>
    /// <param name="progress">Der Rechner für die Fortschrittsmessung.</param>
    /// <param name="options">Die beim Start geprüfte Konfiguration.</param>
    /// <param name="logger">Logger für die serverseitige Diagnose.</param>
    /// <exception cref="ArgumentNullException">Ein Argument ist <c>null</c>.</exception>
    public NavigationController(
        RoutingDBContext context,
        INavigationService navigation,
        RouteProgressCalculator progress,
        IOptions<RoutingOptions> options,
        ILogger<NavigationController> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = context ?? throw new ArgumentNullException(nameof(context));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value.Navigation;
    }

    /// <summary>
    /// Liefert die geplante Route in der Form, die eine örtliche Fortschrittsberechnung im
    /// Fahrer-Client ermöglicht.
    /// </summary>
    /// <remarks>
    /// Einmal je Schicht abrufen. Enthält die Geometrie, je Halt dessen Lage darauf und die
    /// serverseitig konfigurierten Schwellwerte, damit die Entscheidungsregel für eine Neuberechnung
    /// nicht in jeder App-Version erneut geraten wird.
    /// </remarks>
    /// <param name="id">Kennung der Route.</param>
    /// <param name="decodeGeometry">
    /// Wenn <c>true</c>, wird die Geometrie zusätzlich serverseitig dekodiert. Standard <c>false</c>;
    /// die kodierte Polyline ist etwa zehnmal kleiner.
    /// </param>
    /// <param name="ct">Wird bei Client-Trennung ausgelöst.</param>
    /// <returns>Die geplante Route samt Haltelagen.</returns>
    /// <response code="200">Die geplante Route.</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="404">Die Route existiert nicht.</response>
    /// <response code="422">Die Route besitzt keine verwertbare Geometrie und muss neu berechnet werden.</response>
    [HttpGet("routes/{id:int}/plan")]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(RoutePlanResult), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> GetPlan(int id, [FromQuery] bool decodeGeometry, CancellationToken ct)
    {
        try
        {
            var route = await LoadRouteAsync(id, ct).ConfigureAwait(false);
            if (route is null)
            {
                return NotFound();
            }

            var path = _progress.BuildPath(route.Geometry);
            var stops = _progress.LocateStops(path, route.Steps);

            var result = new RoutePlanResult
            {
                RouteId = route.Id,
                Name = route.Name,
                StartDate = route.StartDate,
                EndDate = route.EndDate,
                Geometry = route.Geometry,
                GeometryPointCount = path.Points.Count,
                GeometryLengthMeters = path.TotalLengthMeters,
                ReportedMeters = route.Meters,
                OffRouteToleranceMeters = _progress.OffRouteToleranceMeters,
                ConfirmationFixesRecommended = _progress.ConfirmationFixesRecommended,
                ArrivalRadiusMeters = _progress.ArrivalRadiusMeters,
                Stops = stops.ToList()
            };

            if (decodeGeometry)
            {
                if (path.Points.Count > _options.MaxDecodedPlanPoints)
                {
                    // Ausdrücklich melden statt still eine leere Liste zu liefern: „keine Punkte“ und
                    // „zu viele Punkte“ sind für den Client verschiedene Lagen.
                    result.DecodedGeometryOmitted = true;
                }
                else
                {
                    result.DecodedGeometry = path.Points
                        .Select(p => new GeoPoint { Latitude = p.Latitude, Longitude = p.Longitude })
                        .ToList();
                }
            }

            return Ok(result);
        }
        catch (NavigationException ex)
        {
            return Problem(ex);
        }
        catch (OperationCanceledException)
        {
            return ClientClosedRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler beim Abruf des Routenplans {RouteId}.", id);
            return StatusCode(500, new { error = "Der Routenplan konnte nicht ermittelt werden." });
        }
    }

    /// <summary>
    /// Misst, wo das Fahrzeug auf seiner geplanten Route steht und ob es davon abgewichen ist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rein rechnerisch: <b>kein Aufruf der Routing-Engine</b>, kein Schreibzugriff. Die Position
    /// wird senkrecht auf die gespeicherte Geometrie projiziert; der Querabstand ist die Messgröße
    /// der Abweichungserkennung.
    /// </para>
    /// <para>
    /// <b>Bei erkannter Abweichung <c>targetStopSequence</c> mitsenden.</b> Fährt das Fahrzeug zur
    /// Pause oder um eine Sperrung herum, kann aus der Position nicht abgeleitet werden, welcher
    /// Halt angefahren wird — dieser Zustand liegt beim Client.
    /// </para>
    /// </remarks>
    /// <param name="id">Kennung der Route.</param>
    /// <param name="request">Position und optional der angefahrene Halt.</param>
    /// <param name="ct">Wird bei Client-Trennung ausgelöst.</param>
    /// <returns>Die Fortschrittsmessung.</returns>
    /// <response code="200">Die Fortschrittsmessung.</response>
    /// <response code="400">Ungültige Eingabe oder unbekannter Halt.</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="404">Die Route existiert nicht.</response>
    /// <response code="422">Die Route besitzt keine verwertbare Geometrie.</response>
    [HttpPost("routes/{id:int}/progress")]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(RouteProgressResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(422)]
    public async Task<IActionResult> GetProgress(
        int id,
        [FromBody] RouteProgressRequest request,
        CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var route = await LoadRouteAsync(id, ct).ConfigureAwait(false);
            if (route is null)
            {
                return NotFound();
            }

            var path = _progress.BuildPath(route.Geometry);
            var stops = _progress.LocateStops(path, route.Steps);

            var result = _progress.Calculate(
                route.Id, path, stops, request.Position, request.TargetStopSequence, route.Meters);

            return Ok(result);
        }
        catch (NavigationException ex)
        {
            return Problem(ex);
        }
        catch (OperationCanceledException)
        {
            return ClientClosedRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei der Fortschrittsmessung der Route {RouteId}.", id);
            return StatusCode(500, new { error = "Der Fortschritt konnte nicht ermittelt werden." });
        }
    }

    /// <summary>
    /// Liefert Abbiegehinweise von der gemeldeten Fahrzeugposition zum angefahrenen Halt einer
    /// gespeicherten Route — samt Fortschrittsmessung.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dies ist der Endpunkt für die Neuberechnung.</b> Er berechnet nicht die geplante Route neu,
    /// sondern den Weg von der <em>tatsächlichen</em> Position zum nächsten Ziel. Die geplante
    /// Reihenfolge bleibt unangetastet — ein Umweg zur Pause, eine erzwungene Abbiegung oder eine
    /// Sperrung ändern nichts an der Tour, nur am Weg zum nächsten Halt.
    /// </para>
    /// <para>
    /// Die Zielkoordinate stammt aus dem gespeicherten Halt und kann daher nicht von der Planung
    /// abweichen. Das Kostenmodell ist dasselbe wie bei der Planung
    /// (<see cref="ValhallaCostingProfile"/>) — einschließlich Fahrzeugabmessungen und Gewicht.
    /// </para>
    /// <para>
    /// <b>Gesperrte Straße:</b> Die Koordinate der Sperrung in <c>excludeLocations</c> mitsenden — und
    /// bei jeder weiteren Neuberechnung dieser Schicht erneut. Ohne diese Angabe führt die Engine den
    /// Fahrer bei jeder Neuberechnung erneut in die Absperrung.
    /// </para>
    /// </remarks>
    /// <param name="id">Kennung der Route.</param>
    /// <param name="request">Position, angefahrener Halt und zu meidende Stellen.</param>
    /// <param name="ct">Wird bei Client-Trennung ausgelöst.</param>
    /// <returns>Hinweise, Ziel und Fortschritt.</returns>
    /// <response code="200">Die Abbiegehinweise samt Fortschrittsmessung.</response>
    /// <response code="400">Ungültige Eingabe oder unbekannter Halt.</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="404">Die Route existiert nicht.</response>
    /// <response code="409">Es steht kein Halt mehr aus, zu dem geführt werden könnte.</response>
    /// <response code="422">Keine verwertbare Geometrie, oder für dieses Fahrzeug ist kein Weg zum Ziel befahrbar.</response>
    /// <response code="502">Die Routing-Instanz ist nicht erreichbar oder antwortete fehlerhaft.</response>
    /// <response code="504">Zeitüberschreitung bei der Anfrage an die Routing-Instanz.</response>
    [HttpPost("routes/{id:int}/guidance")]
    [ProducesResponseType(typeof(RouteGuidanceResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(422)]
    [ProducesResponseType(502)]
    [ProducesResponseType(504)]
    public async Task<IActionResult> GetGuidance(
        int id,
        [FromBody] RouteGuidanceRequest request,
        CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var route = await LoadRouteAsync(id, ct).ConfigureAwait(false);
            if (route is null)
            {
                return NotFound();
            }

            var path = _progress.BuildPath(route.Geometry);
            var stops = _progress.LocateStops(path, route.Steps);

            var progress = _progress.Calculate(
                route.Id, path, stops, request.Position, request.TargetStopSequence, route.Meters);

            if (progress.NextStop is not { } target)
            {
                // Kein Ziel mehr: Das ist kein Fehler der Anfrage, sondern ein Zustand der Tour.
                // 409 sagt "die Anfrage passt nicht zum aktuellen Zustand der Ressource".
                return Conflict(new
                {
                    error = "Es steht kein Halt mehr aus. Die Tour ist abgeschlossen.",
                    progress
                });
            }

            var query = new GuidanceQuery(
                request.Position,
                Array.Empty<GeoPoint>(),
                new GeoPoint { Latitude = target.Latitude, Longitude = target.Longitude },
                request.ExcludeLocations,
                request.ExcludePolygons,
                request.Language,
                request.DecodeGeometry);

            var directions = await _navigation.GetDirectionsAsync(query, ct).ConfigureAwait(false);

            return Ok(new RouteGuidanceResult
            {
                Target = target,
                Progress = progress,
                Directions = directions
            });
        }
        catch (NavigationException ex)
        {
            return Problem(ex);
        }
        catch (ValhallaException ex)
        {
            return StatusCode(ex.SuggestedStatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return ClientClosedRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei den Fahrhinweisen der Route {RouteId}.", id);
            return StatusCode(500, new { error = "Die Fahrhinweise konnten nicht berechnet werden." });
        }
    }

    /// <summary>
    /// Liefert Abbiegehinweise zwischen freien Koordinaten, ohne Bezug auf eine gespeicherte Route.
    /// </summary>
    /// <remarks>
    /// Für Fahrten, die nicht Teil einer Tour sind — Anfahrt zum Betriebshof, Weg zur Pause,
    /// Werkstattfahrt. Es gilt dasselbe LKW-Kostenmodell wie bei der Planung; ein Fahrer erhält
    /// daher auch hier keine Strecke, die sein Fahrzeug nach Höhe oder Gewicht nicht befahren darf.
    /// </remarks>
    /// <param name="request">Start, Ziel, Zwischenziele und zu meidende Stellen.</param>
    /// <param name="ct">Wird bei Client-Trennung ausgelöst.</param>
    /// <returns>Die Abbiegehinweise.</returns>
    /// <response code="200">Die Abbiegehinweise.</response>
    /// <response code="400">Ungültige Eingabe oder überschrittene Grenzen.</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="422">Für dieses Fahrzeug ist zwischen den Punkten kein Weg befahrbar.</response>
    /// <response code="502">Die Routing-Instanz ist nicht erreichbar oder antwortete fehlerhaft.</response>
    /// <response code="504">Zeitüberschreitung bei der Anfrage an die Routing-Instanz.</response>
    [HttpPost("directions")]
    [ProducesResponseType(typeof(DirectionsResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(422)]
    [ProducesResponseType(502)]
    [ProducesResponseType(504)]
    public async Task<IActionResult> GetDirections(
        [FromBody] DirectionsRequest request,
        CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var query = new GuidanceQuery(
                request.Position,
                request.Waypoints,
                request.Destination,
                request.ExcludeLocations,
                request.ExcludePolygons,
                request.Language,
                request.DecodeGeometry);

            var result = await _navigation.GetDirectionsAsync(query, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (NavigationException ex)
        {
            return Problem(ex);
        }
        catch (ValhallaException ex)
        {
            return StatusCode(ex.SuggestedStatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return ClientClosedRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei der Berechnung freier Fahrhinweise.");
            return StatusCode(500, new { error = "Die Fahrhinweise konnten nicht berechnet werden." });
        }
    }

    // =============================================================================================
    // Interne Hilfsmethoden
    // =============================================================================================

    /// <summary>
    /// Lädt eine Route mit ihren Halten — schreibgeschützt und ohne die für die Navigation
    /// unnötigen Beziehungen.
    /// </summary>
    /// <remarks>
    /// <c>AsNoTracking</c> und die ausdrückliche Beschränkung auf <c>Steps</c> sind hier keine
    /// Feinoptimierung: Die Navigation ist der am häufigsten aufgerufene Lesepfad des Systems.
    /// <c>Jobs</c>, <c>Skills</c> und <c>Vehicle</c> mitzuladen, wie es
    /// <c>GET /api/Routes/{id}</c> tut, würde je Aufruf mehrere zusätzliche Abfragen auslösen, ohne
    /// dass ein einziges Feld davon verwendet wird.
    /// </remarks>
    private Task<Models.Routes.Route?> LoadRouteAsync(int id, CancellationToken ct)
        => _context.Routes
            .AsNoTracking()
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>Bildet eine <see cref="NavigationException"/> auf die passende Antwort ab.</summary>
    private IActionResult Problem(NavigationException exception)
    {
        if (exception.SuggestedStatusCode >= StatusCodes.Status500InternalServerError)
        {
            // Nur echte Serverfehler werden protokolliert; fachliche Ablehnungen sind kein Vorfall.
            _logger.LogError(exception, "Serverseitiger Fehler in der Navigation.");
        }

        return StatusCode(exception.SuggestedStatusCode, new { error = exception.Message });
    }

    /// <summary>
    /// Antwort auf einen Abbruch durch den Aufrufer: 499 (Client Closed Request), kein Serverfehler.
    /// </summary>
    /// <remarks>
    /// Ein Fahrer-Client bricht Anfragen bei Funklöchern regelmäßig ab. Diese Fälle als <c>500</c>
    /// zu zählen würde die Fehlerstatistik unbrauchbar machen — genau die Behandlung, die auch der
    /// Map-Matching-Controller verwendet.
    /// </remarks>
    private IActionResult ClientClosedRequest() => StatusCode(499);
}
