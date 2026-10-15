using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Services;

namespace SmartRoutingAPI.Controllers;

// -------------------------------------------------------------------------------------------------
// Recurring (Frequency) routes — plain CRUD, DTO responses, paginated.
// -------------------------------------------------------------------------------------------------
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewRoutes)]
public class RecurringRoutesController : ControllerBase
{
    private readonly RoutingDBContext _context;

    public RecurringRoutesController(RoutingDBContext context)
    {
        _context = context;
    }

    /// <summary>Ruft alle wiederholenden Routen ab (paginiert).</summary>
    /// <response code="200">Seite von wiederholenden Routen.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<FrequencyRouteResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<FrequencyRouteResponse>>> GetAllRoutes([FromQuery] PageQuery page, CancellationToken ct)
    {
        var baseQuery = _context.FrequencyRoutes.AsNoTracking().OrderBy(r => r.Id);
        var total = await baseQuery.CountAsync(ct);

        var routes = await baseQuery
            .Skip(page.Skip).Take(page.Take)
            .Include(r => r.Skills)
            .Include(r => r.Vehicle)!.ThenInclude(v => v.Skills)
            .AsSplitQuery()
            .ToListAsync(ct);

        var items = routes.Select(r => r.ToResponse()).ToList();
        return Ok(new PagedResult<FrequencyRouteResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Ruft eine spezifische wiederholende Route anhand ihrer ID ab.</summary>
    /// <response code="200">Die angeforderte wiederholende Route.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(FrequencyRouteResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<FrequencyRouteResponse>> GetRoute(int id, CancellationToken ct)
    {
        var route = await _context.FrequencyRoutes
            .AsNoTracking()
            .Include(r => r.Skills)
            .Include(r => r.Vehicle)!.ThenInclude(v => v.Skills)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return route is null ? NotFound() : Ok(route.ToResponse());
    }

    /// <summary>Erstellt eine neue wiederholende Route.</summary>
    /// <response code="201">Die neu erstellte Route.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPost]
    [ProducesResponseType(typeof(FrequencyRouteResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<ActionResult<FrequencyRouteResponse>> CreateRoute([FromBody] RecurringRouteDTO route, CancellationToken ct)
    {
        var skills = await _context.Skills.Where(s => route.SkillsIdList.Contains(s.Id)).ToListAsync(ct);
        var vehicle = await _context.Vehicles.FindAsync(new object[] { route.VehicleId }, ct);

        if (vehicle == null || skills.Count != route.SkillsIdList.Distinct().Count())
        {
            return ValidationProblem("Invalid vehicle or skill ID");
        }

        var r = new FrequencyRoute
        {
            Color = route.Color,
            Description = route.Description,
            Name = route.Name,
            EndLatitude = route.EndLatitude,
            EndLongitude = route.EndLongitude,
            ExternalReference = route.ExternalReference,
            Frequency = route.Frequency,
            Skills = skills,
            StartDate = route.StartDate,
            EndDate = route.EndDate,
            StartLatitude = route.StartLatitude,
            StartLongitude = route.StartLongitude,
            Vehicle = vehicle
        };

        _context.FrequencyRoutes.Add(r);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetRoute), new { id = r.Id }, r.ToResponse());
    }

    /// <summary>Aktualisiert eine vorhandene wiederholende Route.</summary>
    /// <response code="200">Die aktualisierte Route.</response>
    /// <response code="404">Nicht gefunden.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(FrequencyRouteResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<ActionResult<FrequencyRouteResponse>> UpdateRoute(int id, [FromBody] RecurringRouteDTO route, CancellationToken ct)
    {
        var existingRoute = await _context.FrequencyRoutes
            .Include(r => r.Skills)
            .Include(r => r.Vehicle)!.ThenInclude(v => v.Skills)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (existingRoute == null)
        {
            return NotFound();
        }

        var skills = await _context.Skills.Where(s => route.SkillsIdList.Contains(s.Id)).ToListAsync(ct);
        var vehicle = await _context.Vehicles.Include(v => v.Skills).FirstOrDefaultAsync(v => v.Id == route.VehicleId, ct);

        if (vehicle == null || skills.Count != route.SkillsIdList.Distinct().Count())
        {
            return ValidationProblem("Invalid vehicle or skill ID");
        }

        existingRoute.Name = route.Name;
        existingRoute.Description = route.Description;
        existingRoute.ExternalReference = route.ExternalReference;
        existingRoute.Color = route.Color;
        existingRoute.Frequency = route.Frequency;
        existingRoute.StartDate = route.StartDate;
        existingRoute.EndDate = route.EndDate;
        existingRoute.StartLatitude = route.StartLatitude;
        existingRoute.StartLongitude = route.StartLongitude;
        existingRoute.EndLatitude = route.EndLatitude;
        existingRoute.EndLongitude = route.EndLongitude;
        existingRoute.Vehicle = vehicle;

        existingRoute.Skills.Clear();
        foreach (var skill in skills) existingRoute.Skills.Add(skill);

        await _context.SaveChangesAsync(ct);

        return Ok(existingRoute.ToResponse());
    }

    /// <summary>Löscht eine vorhandene wiederholende Route.</summary>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> DeleteRoute(int id, CancellationToken ct)
    {
        var route = await _context.FrequencyRoutes.FindAsync(new object[] { id }, ct);
        if (route == null)
        {
            return NotFound();
        }

        _context.FrequencyRoutes.Remove(route);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }
}

// -------------------------------------------------------------------------------------------------
// Concrete routes — typed DTO reads; writes are enqueued (202) because they run VROOM optimisation.
// -------------------------------------------------------------------------------------------------
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewRoutes)]
public class RoutesController : ControllerBase
{
    private readonly RoutingDBContext _context;
    private readonly IRouteOptimizationQueue _queue;

    public RoutesController(RoutingDBContext context, IRouteOptimizationQueue queue)
    {
        _context = context;
        _queue = queue;
    }

    /// <summary>Ruft aktive Routen ab (paginiert, kompakte Darstellung ohne Jobs/Steps).</summary>
    /// <response code="200">Seite von Routen-Zusammenfassungen.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<RouteSummaryResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<RouteSummaryResponse>>> GetAllRoutes([FromQuery] PageQuery page, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.Date.AddDays(-1);

        var baseQuery = _context.Routes
            .AsNoTracking()
            .Where(r => r.EndDate >= since)
            .OrderBy(r => r.StartDate);

        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .Skip(page.Skip).Take(page.Take)
            .Select(r => new RouteSummaryResponse(
                r.Id, r.Name, r.Description, r.Color, r.StartDate, r.EndDate,
                r.Jobs!.Count, r.Steps!.Count, r.Meters, r.Duration,
                r.FillShift, r.Co2eGrams,
                r.Vehicle != null ? r.Vehicle.Id : (int?)null))
            .ToListAsync(ct);

        return Ok(new PagedResult<RouteSummaryResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Ruft eine spezifische Route inklusive Jobs, Skills, Fahrzeug und Schritten ab.</summary>
    /// <response code="200">Die angeforderte Route.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(RouteResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<RouteResponse>> GetRoute(int id, CancellationToken ct)
    {
        var route = await _context.Routes
            .AsNoTracking()
            .AsSplitQuery()
            .Include(r => r.Jobs)!.ThenInclude(j => j.Skill)
            .Include(r => r.Skills)
            .Include(r => r.Vehicle)!.ThenInclude(v => v.Skills)
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return route is null ? NotFound() : Ok(route.ToResponse());
    }

    /// <summary>
    /// Reicht die Erstellung einer neuen (optimierten) Route ein. Läuft asynchron.
    /// </summary>
    /// <remarks>
    /// Antwortet mit <c>202 Accepted</c> und einer Job-ID. Der Fortschritt wird über
    /// <c>GET /api/Routes/jobs/{jobId}</c> abgefragt; das Ergebnis (die Route) erscheint dort,
    /// sobald der Status <c>Succeeded</c> ist.
    /// </remarks>
    /// <response code="202">Optimierung eingereiht; Job-ID zurückgegeben.</response>
    /// <response code="400">Ungültige Eingabedaten (Fahrzeug/Skills existieren nicht).</response>
    [HttpPost]
    [ProducesResponseType(typeof(OptimizationJobResponse), 202)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> CreateRoute([FromBody] RouteDTO route, CancellationToken ct)
    {
        if (!await ValidateVehicleAndSkillsAsync(route.VehicleId, route.SkillsIdList, ct))
        {
            return ValidationProblem("Invalid vehicle or skill ID");
        }

        var job = _queue.EnqueueCreate(route);
        return Accepted($"/api/Routes/jobs/{job.JobId}", job);
    }

    /// <summary>Reicht das Hinzufügen/Entfernen von Jobs einer Route zur Neuberechnung ein (persistierend).</summary>
    /// <response code="202">Optimierung eingereiht.</response>
    /// <response code="404">Route nicht gefunden.</response>
    [HttpPatch("{id:int}/jobs")]
    [ProducesResponseType(typeof(OptimizationJobResponse), 202)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> PatchJobs(int id, [FromBody] RouteJobPatch patch, CancellationToken ct)
    {
        if (!await _context.Routes.AnyAsync(r => r.Id == id, ct))
        {
            return NotFound();
        }

        var job = _queue.EnqueueJobChange(id, patch, saveToDb: true);
        return Accepted($"/api/Routes/jobs/{job.JobId}", job);
    }

    /// <summary>Reicht eine Vorschau-Neuberechnung mit Job-Änderungen ein (nicht persistierend).</summary>
    /// <remarks>Bewusst ein POST (keine GET), da eine schwere Berechnung ausgelöst wird.</remarks>
    /// <response code="202">Vorschau-Berechnung eingereiht.</response>
    /// <response code="404">Route nicht gefunden.</response>
    [HttpPost("{id:int}/preview")]
    [ProducesResponseType(typeof(OptimizationJobResponse), 202)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> PreviewJobs(int id, [FromBody] RouteJobPatch patch, CancellationToken ct)
    {
        if (!await _context.Routes.AnyAsync(r => r.Id == id, ct))
        {
            return NotFound();
        }

        var job = _queue.EnqueueJobChange(id, patch, saveToDb: false);
        return Accepted($"/api/Routes/jobs/{job.JobId}", job);
    }

    /// <summary>Reicht eine Metadaten-Änderung samt Neuberechnung der Route ein.</summary>
    /// <response code="202">Optimierung eingereiht.</response>
    /// <response code="404">Route nicht gefunden.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPut("{id:int}/metadata")]
    [ProducesResponseType(typeof(OptimizationJobResponse), 202)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> UpdateRouteMetadata(int id, [FromBody] RouteMetadataDTO metadata, CancellationToken ct)
    {
        if (!await _context.Routes.AnyAsync(r => r.Id == id, ct))
        {
            return NotFound();
        }

        if (!await ValidateVehicleAndSkillsAsync(metadata.VehicleId, metadata.SkillsIdList, ct))
        {
            return ValidationProblem("Invalid vehicle or skill ID");
        }

        var job = _queue.EnqueueMetadata(id, metadata);
        return Accepted($"/api/Routes/jobs/{job.JobId}", job);
    }

    /// <summary>Fragt den Status/das Ergebnis eines eingereihten Optimierungs-Auftrags ab.</summary>
    /// <response code="200">Aktueller Status (inkl. Ergebnis, sobald erfolgreich).</response>
    /// <response code="404">Unbekannte Job-ID.</response>
    [HttpGet("jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(OptimizationJobResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public IActionResult GetJob(Guid jobId)
    {
        var job = _queue.Get(jobId);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Löscht eine vorhandene Route.</summary>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> DeleteRoute(int id, CancellationToken ct)
    {
        var route = await _context.Routes.FindAsync(new object[] { id }, ct);
        if (route == null)
        {
            return NotFound();
        }

        _context.Routes.Remove(route);
        await _context.SaveChangesAsync(ct); // exceptions now bubble to the global handler → 500
        return NoContent();
    }

    private async Task<bool> ValidateVehicleAndSkillsAsync(int vehicleId, IEnumerable<int> skillIds, CancellationToken ct)
    {
        var ids = skillIds.Distinct().ToList();
        var vehicleExists = await _context.Vehicles.AnyAsync(v => v.Id == vehicleId, ct);
        if (!vehicleExists)
        {
            return false;
        }

        var skillCount = await _context.Skills.CountAsync(s => ids.Contains(s.Id), ct);
        return skillCount == ids.Count;
    }
}
