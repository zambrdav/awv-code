using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Vehicles;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Base;

namespace SmartRoutingAPI.Controllers;

public class SkillRequest : BaseData
{
}

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewSkills)]
public class SkillsController : ControllerBase
{
    private readonly RoutingDBContext _context;

    public SkillsController(RoutingDBContext context)
    {
        _context = context;
    }

    /// <summary>Ruft alle Fähigkeiten ab (paginiert).</summary>
    /// <response code="200">Seite von Fähigkeiten.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<SkillResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<SkillResponse>>> GetAllSkills([FromQuery] PageQuery page, CancellationToken ct)
    {
        var baseQuery = _context.Skills.AsNoTracking().OrderBy(s => s.Id);
        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .Skip(page.Skip).Take(page.Take)
            .Select(s => new SkillResponse(s.Id, s.Name, s.Description, s.ExternalReference))
            .ToListAsync(ct);

        return Ok(new PagedResult<SkillResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Ruft eine spezifische Fähigkeit anhand ihrer ID ab.</summary>
    /// <response code="200">Die angeforderte Fähigkeit.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(SkillResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<SkillResponse>> GetSkill(int id, CancellationToken ct)
    {
        var skill = await _context.Skills.FindAsync(new object[] { id }, ct);
        return skill is null ? NotFound() : Ok(skill.ToResponse());
    }

    /// <summary>Erstellt eine neue Fähigkeit.</summary>
    /// <response code="201">Die neu erstellte Fähigkeit.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPost]
    [ProducesResponseType(typeof(SkillResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditSkills)] // was EditVehicles — corrected (§3.5 B4)
    public async Task<ActionResult<SkillResponse>> CreateSkill([FromBody] SkillRequest skill, CancellationToken ct)
    {
        var newSkill = new Skill
        {
            Description = skill.Description,
            Name = skill.Name,
            ExternalReference = skill.ExternalReference
        };

        _context.Skills.Add(newSkill);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetSkill), new { id = newSkill.Id }, newSkill.ToResponse());
    }

    /// <summary>Aktualisiert eine vorhandene Fähigkeit.</summary>
    /// <response code="200">Die aktualisierte Fähigkeit.</response>
    /// <response code="404">Nicht gefunden.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(SkillResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditSkills)]
    public async Task<ActionResult<SkillResponse>> UpdateSkill(int id, [FromBody] SkillRequest skill, CancellationToken ct)
    {
        var existingSkill = await _context.Skills.FindAsync(new object[] { id }, ct);
        if (existingSkill == null)
        {
            return NotFound();
        }

        existingSkill.Name = skill.Name;
        existingSkill.Description = skill.Description;
        existingSkill.ExternalReference = skill.ExternalReference;

        await _context.SaveChangesAsync(ct);
        return Ok(existingSkill.ToResponse());
    }

    /// <summary>Löscht eine vorhandene Fähigkeit.</summary>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditSkills)]
    public async Task<IActionResult> DeleteSkill(int id, CancellationToken ct)
    {
        var skill = await _context.Skills.FindAsync(new object[] { id }, ct);
        if (skill == null)
        {
            return NotFound();
        }

        _context.Skills.Remove(skill);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }
}
