using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Vehicles;
using SmartRoutingAPI.Models.Accounts;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SmartRoutingAPI.Models.Base;

namespace SmartRoutingAPI.Controllers;

public class VehicleData : BaseData
{
    /// <summary>
    /// Nutzlast in Kapazitätseinheiten – dieselbe Einheit wie <c>Job.Capacity</c>.
    /// </summary>
    /// <remarks>
    /// Geändert von <c>long</c> auf <c>double</c>: Zwei Zahlentypen für dieselbe physikalische
    /// Größe sind eine Fehlerquelle, und eine Nutzlast von 12,5 Einheiten war nicht darstellbar.
    /// Negative Werte sind nicht länger zulässig – eine negative Nutzlast hat keine Bedeutung und
    /// hätte jede Ladungsrechnung unbrauchbar gemacht.
    /// </remarks>
    [Description("Nutzlast in Kapazitätseinheiten (gleiche Einheit wie Job.Capacity).")]
    [Range(0.0, 1_000_000.0, ErrorMessage = "Die Kapazität muss zwischen 0 und 1.000.000 liegen.")]
    public double Capacity { get; set; }

    [Required, Description("Alle Fähigkeiten (Skill-IDs), für die das Fahrzeug fahren kann.")]
    public ICollection<int> SkillIdList { get; set; } = new List<int>();
}

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewVehicles)]
public class VehiclesController : ControllerBase
{
    private readonly RoutingDBContext _context;

    public VehiclesController(RoutingDBContext context)
    {
        _context = context;
    }

    /// <summary>Ruft alle Fahrzeuge ab (paginiert).</summary>
    /// <response code="200">Seite von Fahrzeugen.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<VehicleResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<VehicleResponse>>> GetAllVehicles([FromQuery] PageQuery page, CancellationToken ct)
    {
        var baseQuery = _context.Vehicles.AsNoTracking().OrderBy(v => v.Id);
        var total = await baseQuery.CountAsync(ct);

        var vehicles = await baseQuery
            .Skip(page.Skip).Take(page.Take)
            .Include(v => v.Skills)
            .AsSplitQuery()
            .ToListAsync(ct);

        var items = vehicles.Select(v => v.ToResponse()).ToList();
        return Ok(new PagedResult<VehicleResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Ruft ein spezifisches Fahrzeug anhand seiner ID ab.</summary>
    /// <response code="200">Das angeforderte Fahrzeug.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(VehicleResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<VehicleResponse>> GetVehicle(int id, CancellationToken ct)
    {
        var vehicle = await _context.Vehicles.AsNoTracking()
            .Include(v => v.Skills)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        return vehicle is null ? NotFound() : Ok(vehicle.ToResponse());
    }

    /// <summary>Erstellt ein neues Fahrzeug.</summary>
    /// <response code="201">Das neu erstellte Fahrzeug.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPost]
    [ProducesResponseType(typeof(VehicleResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditVehicles)]
    public async Task<ActionResult<VehicleResponse>> CreateVehicle([FromBody] VehicleData vehicle, CancellationToken ct)
    {
        // Filter in the DB instead of loading the whole Skills table into memory (§4.5 / P5).
        var skills = await _context.Skills
            .Where(s => vehicle.SkillIdList.Contains(s.Id))
            .ToListAsync(ct);

        var vehicleToAdd = new Vehicle
        {
            Capacity = vehicle.Capacity,
            Skills = skills,
            Description = vehicle.Description,
            ExternalReference = vehicle.ExternalReference,
            Name = vehicle.Name
        };

        _context.Vehicles.Add(vehicleToAdd);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetVehicle), new { id = vehicleToAdd.Id }, vehicleToAdd.ToResponse());
    }

    /// <summary>Aktualisiert ein vorhandenes Fahrzeug.</summary>
    /// <response code="200">Das aktualisierte Fahrzeug.</response>
    /// <response code="404">Nicht gefunden.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(VehicleResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditVehicles)]
    public async Task<ActionResult<VehicleResponse>> UpdateVehicle(int id, [FromBody] VehicleData vehicle, CancellationToken ct)
    {
        var existingVehicle = await _context.Vehicles
            .Include(v => v.Skills)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        if (existingVehicle == null)
        {
            return NotFound();
        }

        existingVehicle.Name = vehicle.Name;
        existingVehicle.Description = vehicle.Description;
        existingVehicle.ExternalReference = vehicle.ExternalReference;
        existingVehicle.Capacity = vehicle.Capacity;

        var skills = await _context.Skills
            .Where(s => vehicle.SkillIdList.Contains(s.Id))
            .ToListAsync(ct);
        existingVehicle.Skills = skills;

        await _context.SaveChangesAsync(ct);

        return Ok(existingVehicle.ToResponse());
    }

    /// <summary>Löscht ein vorhandenes Fahrzeug.</summary>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditVehicles)]
    public async Task<IActionResult> DeleteVehicle(int id, CancellationToken ct)
    {
        var vehicle = await _context.Vehicles.FindAsync(new object[] { id }, ct);
        if (vehicle == null)
        {
            return NotFound();
        }

        _context.Vehicles.Remove(vehicle);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }
}
