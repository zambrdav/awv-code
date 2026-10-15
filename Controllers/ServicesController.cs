using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Services;

namespace SmartRoutingAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class ServicesController : ControllerBase
{
    private readonly RoutingDBContext _context;
    private readonly RouteOptimizationService _optimizationService;
    private readonly ILogger<ServicesController> _logger;

    public ServicesController(
        RoutingDBContext context,
        RouteOptimizationService optimizationService,
        ILogger<ServicesController> logger)
    {
        _context = context;
        _optimizationService = optimizationService;
        _logger = logger;
    }

    /// <summary>
    /// Materialises all recurring ("Frequency") routes that fall due tomorrow into concrete routes.
    /// </summary>
    /// <remarks>
    /// Intended to be triggered by a scheduler/cron. Runs the optimisation synchronously per due route
    /// via the shared <see cref="RouteOptimizationService"/> (no controller is instantiated by hand).
    /// </remarks>
    /// <response code="200">All due routes were created.</response>
    /// <response code="207">Only some routes could be created.</response>
    /// <response code="500">Unexpected failure.</response>
    [HttpPost("RecurringRoutes")]
    [ProducesResponseType(typeof(List<RouteResponse>), 200)]
    [ProducesResponseType(typeof(List<RouteResponse>), 207)]
    [ProducesResponseType(500)]
    [CheckApplicationPermission(ApplicationRights.EditRoutes)]
    public async Task<IActionResult> CreateRecurringRoutes(CancellationToken ct)
    {
        var recurringRoutes = await _context.FrequencyRoutes
            .Include(fr => fr.Skills)
            .Include(fr => fr.Vehicle)
            .AsNoTracking()
            .ToListAsync(ct);

        var dateForAdd = DateTime.UtcNow.Date.AddDays(1);

        var routesStartingNextDay = new List<FrequencyRoute>();
        foreach (var route in recurringRoutes)
        {
            var routeDate = route.StartDate.Date;
            while (routeDate < dateForAdd)
            {
                routeDate += route.Frequency;
                if (routeDate > route.EndDate) break;
            }

            if (routeDate == dateForAdd)
            {
                routesStartingNextDay.Add(route);
            }
        }

        var created = new List<RouteResponse>();
        foreach (var route in routesStartingNextDay)
        {
            var dto = new RouteDTO
            {
                Name = route.Name,
                Description = route.Description ?? "",
                ExternalReference = route.ExternalReference ?? "",
                StartDate = dateForAdd.Date + route.StartDate.TimeOfDay,
                EndDate = dateForAdd.Date + route.EndDate.TimeOfDay,
                SkillsIdList = route.Skills.Select(s => s.Id).ToList(),
                Color = route.Color,
                VehicleId = route.Vehicle.Id,
                StartLatitude = route.StartLatitude,
                EndLatitude = route.EndLatitude,
                StartLongitude = route.StartLongitude,
                EndLongitude = route.EndLongitude,

                // Die Vorgabe der Frequenzroute wird übernommen und kann an der erzeugten Route
                // weiterhin einzeln geändert werden.
                FillShift = route.FillShift
            };

            try
            {
                var r = await _optimizationService.CreateRouteInternalAsync(dto, ct);
                if (r != null) created.Add(r.ToResponse());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to materialise recurring route {RouteId}.", route.Id);
            }
        }

        if (created.Count != routesStartingNextDay.Count)
        {
            return StatusCode(207, created);
        }

        return Ok(created);
    }
}
