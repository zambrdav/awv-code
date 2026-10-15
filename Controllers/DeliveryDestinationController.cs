using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Base;
using SmartRoutingAPI.Models.Accounts;

namespace SmartRoutingAPI.Controllers;

public class DeliveryDestinationDTO : BaseData
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double MaximumCapacity { get; set; }
    public List<int> SkillsIdList { get; set; } = new();

    /// <summary>
    /// Feste Bedienzeit einer Entleerung in Sekunden. <c>null</c> übernimmt die globale Vorgabe.
    /// </summary>
    /// <remarks>
    /// Maßgeblicher Regler gegen unnötige Zwischenentleerungen: Je teurer eine Entleerung in Zeit
    /// ist, desto eher füllt der Optimierer das Fahrzeug aus, statt einen zusätzlichen Halt
    /// einzuplanen.
    /// </remarks>
    [System.ComponentModel.DataAnnotations.Range(0, 28800,
        ErrorMessage = "Die Bedienzeit muss zwischen 0 und 28800 Sekunden liegen.")]
    public int? ServiceTimeSeconds { get; set; }

    /// <summary>Zusätzliche Bedienzeit je abgeladener Kapazitätseinheit in Sekunden.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 3600,
        ErrorMessage = "Der Wert muss zwischen 0 und 3600 Sekunden liegen.")]
    public double? ServiceSecondsPerUnit { get; set; }
}

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewJobs)]
public class DeliveryDestinationsController : ControllerBase
{
    private readonly RoutingDBContext _context;

    public DeliveryDestinationsController(RoutingDBContext context)
    {
        _context = context;
    }

    /// <summary>Ruft alle Abladestellen ab (paginiert).</summary>
    /// <response code="200">Seite von Abladestellen.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<DeliveryDestinationResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<DeliveryDestinationResponse>>> GetAllDeliveryDestinations([FromQuery] PageQuery page, CancellationToken ct)
    {
        var baseQuery = _context.DeliveryDestinations.AsNoTracking().OrderBy(d => d.Id);
        var total = await baseQuery.CountAsync(ct);

        var destinations = await baseQuery
            .Skip(page.Skip).Take(page.Take)
            .Include(d => d.Skills)
            .AsSplitQuery()
            .ToListAsync(ct);

        var items = destinations.Select(d => d.ToResponse()).ToList();
        return Ok(new PagedResult<DeliveryDestinationResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Ruft eine spezifische Abladestelle anhand ihrer ID ab.</summary>
    /// <response code="200">Die angeforderte Abladestelle.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(DeliveryDestinationResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<DeliveryDestinationResponse>> GetDeliveryDestination(int id, CancellationToken ct)
    {
        var destination = await _context.DeliveryDestinations.AsNoTracking()
            .Include(d => d.Skills)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        return destination is null ? NotFound() : Ok(destination.ToResponse());
    }

    /// <summary>Erstellt eine neue Abladestelle.</summary>
    /// <response code="201">Die neu erstellte Abladestelle.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPost]
    [ProducesResponseType(typeof(DeliveryDestinationResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<ActionResult<DeliveryDestinationResponse>> CreateDeliveryDestination([FromBody] DeliveryDestinationDTO destination, CancellationToken ct)
    {
        var skills = await _context.Skills.Where(s => destination.SkillsIdList.Contains(s.Id)).ToListAsync(ct);

        var nDestination = new DeliveryDestination
        {
            Description = destination.Description,
            ExternalReference = destination.ExternalReference,
            Latitude = destination.Latitude,
            Longitude = destination.Longitude,
            Name = destination.Name,
            MaximumCapacity = destination.MaximumCapacity,
            Skills = skills,
            ServiceTimeSeconds = destination.ServiceTimeSeconds,
            ServiceSecondsPerUnit = destination.ServiceSecondsPerUnit
        };

        _context.DeliveryDestinations.Add(nDestination);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetDeliveryDestination), new { id = nDestination.Id }, nDestination.ToResponse());
    }

    /// <summary>Aktualisiert eine bestehende Abladestelle.</summary>
    /// <response code="200">Die aktualisierte Abladestelle.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(DeliveryDestinationResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<ActionResult<DeliveryDestinationResponse>> UpdateDeliveryDestination(int id, [FromBody] DeliveryDestinationDTO destination, CancellationToken ct)
    {
        var existingDestination = await _context.DeliveryDestinations
            .Include(d => d.Skills)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (existingDestination == null)
        {
            return NotFound();
        }

        var skills = await _context.Skills.Where(s => destination.SkillsIdList.Contains(s.Id)).ToListAsync(ct);

        // Explicit mapping instead of CurrentValues.SetValues(dto) (§3.3 ST6).
        existingDestination.Name = destination.Name;
        existingDestination.Description = destination.Description;
        existingDestination.ExternalReference = destination.ExternalReference;
        existingDestination.Latitude = destination.Latitude;
        existingDestination.Longitude = destination.Longitude;
        existingDestination.MaximumCapacity = destination.MaximumCapacity;
        existingDestination.ServiceTimeSeconds = destination.ServiceTimeSeconds;
        existingDestination.ServiceSecondsPerUnit = destination.ServiceSecondsPerUnit;

        existingDestination.Skills.Clear();
        foreach (var skill in skills) existingDestination.Skills.Add(skill);

        await _context.SaveChangesAsync(ct);
        return Ok(existingDestination.ToResponse());
    }

    /// <summary>Löscht eine Abladestelle.</summary>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<IActionResult> DeleteDeliveryDestination(int id, CancellationToken ct)
    {
        var destination = await _context.DeliveryDestinations.FindAsync(new object[] { id }, ct);
        if (destination == null)
        {
            return NotFound();
        }

        _context.DeliveryDestinations.Remove(destination);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }
}
