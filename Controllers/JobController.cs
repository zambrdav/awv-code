using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Base;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SmartRoutingAPI.Models.Accounts;

namespace SmartRoutingAPI.Controllers;

/// <summary>
/// Payload zum Anlegen und Aktualisieren eines Sammelpunkts.
/// </summary>
/// <remarks>
/// <b>Entfallene Felder gegenüber der Vorversion:</b> <c>allowManualAssignment</c> und
/// <c>delivery</c>. Beide sind vollständig entfernt; siehe <see cref="Job"/> für die Begründung.
/// Wird eines der beiden noch gesendet, wird es ignoriert – deshalb weist dieser Endpunkt
/// unbekannte Felder ausdrücklich zurück (siehe <c>Program</c>, <c>UnmappedMemberHandling</c>),
/// damit ein veralteter Aufrufer einen Fehler erhält und nicht stillschweigend Daten verliert.
/// </remarks>
public class JobDTO : BaseData
{
    /// <summary>Erwartete Sammelmenge in Kapazitätseinheiten (gleiche Einheit wie Vehicle.Capacity).</summary>
    [Range(0.0, 1_000_000.0, ErrorMessage = "Die Sammelmenge muss zwischen 0 und 1.000.000 liegen.")]
    public double Capacity { get; set; }

    /// <summary>
    /// Priorität 0–100.
    /// </summary>
    /// <remarks>
    /// Werte bis <c>Routing:Planner:MinimumPriorityFactor * 100</c> (Vorgabe 25) sind
    /// wirkungsgleich. Für eine feinere Abstufung ist der Bereich oberhalb von 25 zu nutzen.
    /// </remarks>
    [Range(0, 100)]
    public int Priority { get; set; }

    [Range(-90.0, 90.0, ErrorMessage = "Der Breitengrad muss zwischen -90 und 90 liegen.")]
    public double Latitude { get; set; }

    [Range(-180.0, 180.0, ErrorMessage = "Der Längengrad muss zwischen -180 und 180 liegen.")]
    public double Longitude { get; set; }

    /// <summary>Die erforderliche Fraktion. Muss eine bestehende Skill-ID sein.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "SkillId muss positiv sein.")]
    public int SkillId { get; set; }

    /// <summary>
    /// Bedienzeit dieses Sammelpunkts in Sekunden. <c>null</c> übernimmt die globale Vorgabe.
    /// </summary>
    [Range(0, 7200, ErrorMessage = "Die Bedienzeit muss zwischen 0 und 7200 Sekunden liegen.")]
    public int? ServiceTimeSeconds { get; set; }
}

/// <summary>
/// Payload zum Setzen der clientseitigen Rohdaten eines Sammelpunkts (<see cref="Job.Data"/>).
/// </summary>
/// <remarks>
/// Bewusst vom allgemeinen <see cref="JobDTO"/> getrennt: Diese Daten werden weder beim Anlegen
/// noch beim Aktualisieren eines Sammelpunkts angefasst, sondern ausschließlich über
/// <c>POST /api/Jobs/{id}/data</c> gesetzt.
/// </remarks>
public sealed class JobDataDTO
{
    /// <summary>
    /// Die zu speichernden Rohdaten, üblicherweise (aber nicht zwingend) JSON. Der Server
    /// validiert den Inhalt nicht als JSON und interpretiert ihn nicht. <c>null</c> oder ein
    /// leerer String löschen vorhandene Rohdaten.
    /// </summary>
    [MaxLength(Job.MaxDataLength, ErrorMessage = "Data darf höchstens 16.000 Zeichen umfassen.")]
    public string? Data { get; set; }
}

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewJobs)]
public class JobsController : ControllerBase
{
    private readonly RoutingDBContext _context;

    public JobsController(RoutingDBContext context)
    {
        _context = context;
    }

    /// <summary>Ruft alle Sammelpunkte ab (paginiert).</summary>
    /// <remarks>
    /// <b>Wichtig für Fremdsysteme:</b> Die Antwort ist eine Seiten-Hülle
    /// (<c>{ items, page, pageSize, totalCount, totalPages, hasNextPage, hasPreviousPage }</c>) und
    /// <b>kein</b> Array. Die Vorgabe für <c>pageSize</c> ist 50, das Maximum 200. Ein Importeur muss
    /// über alle Seiten laufen; andernfalls sieht er nur die ersten 50 Sammelpunkte.
    /// </remarks>
    /// <response code="200">Seite von Sammelpunkten.</response>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<JobResponse>), 200)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<PagedResult<JobResponse>>> GetAllJobs([FromQuery] PageQuery query, CancellationToken ct)
    {
        var baseQuery = _context.Jobs.AsNoTracking().OrderBy(j => j.Id);
        var total = await baseQuery.CountAsync(ct);

        var jobs = await baseQuery
            .Skip(query.Skip).Take(query.Take)
            .Include(j => j.Skill)
            .AsSplitQuery()
            .ToListAsync(ct);

        var items = jobs.Select(j => j.ToResponse()).ToList();
        return Ok(new PagedResult<JobResponse>(items, query.Page, query.PageSize, total));
    }

    /// <summary>
    /// Sucht einen Sammelpunkt über seine externe Kennung.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dieser Endpunkt existiert für Fremdsysteme, die ihre eigenen Kennungen abgleichen. Bisher
    /// musste ein Importeur dafür <em>alle</em> Sammelpunkte laden und im Speicher suchen – bei
    /// gleichzeitig paginierter Liste ein Verfahren, das mit wachsendem Bestand zwangsläufig
    /// unvollständig wird.
    /// </para>
    /// <para>
    /// Die externe Kennung ist nicht als eindeutig erzwungen; gibt es mehrere Treffer, wird der
    /// mit der kleinsten ID geliefert.
    /// </para>
    /// </remarks>
    /// <param name="externalReference">Die zu suchende externe Kennung.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <response code="200">Der gefundene Sammelpunkt.</response>
    /// <response code="404">Keine Entsprechung.</response>
    [HttpGet("by-external-reference/{externalReference}")]
    [ProducesResponseType(typeof(JobResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<JobResponse>> GetJobByExternalReference(
        [FromRoute, Required, MaxLength(512)] string externalReference,
        CancellationToken ct)
    {
        var job = await _context.Jobs.AsNoTracking()
            .Include(j => j.Skill)
            .Where(j => j.ExternalReference == externalReference)
            .OrderBy(j => j.Id)
            .FirstOrDefaultAsync(ct);

        return job is null ? NotFound() : Ok(job.ToResponse());
    }

    /// <summary>Ruft einen spezifischen Sammelpunkt anhand seiner ID ab.</summary>
    /// <response code="200">Der angeforderte Sammelpunkt.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(JobResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<JobResponse>> GetJob(int id, CancellationToken ct)
    {
        var job = await _context.Jobs.AsNoTracking()
            .Include(j => j.Skill)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return job is null ? NotFound() : Ok(job.ToResponse());
    }

    /// <summary>Erstellt einen neuen Sammelpunkt.</summary>
    /// <response code="201">Der neu erstellte Sammelpunkt.</response>
    /// <response code="400">Ungültige Eingabedaten (z. B. unbekannte Skill-ID).</response>
    [HttpPost]
    [ProducesResponseType(typeof(JobResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<ActionResult<JobResponse>> CreateJob([FromBody] JobDTO job, CancellationToken ct)
    {
        var skill = await _context.Skills.FirstOrDefaultAsync(s => s.Id == job.SkillId, ct);
        if (skill == null)
        {
            return ValidationProblem($"Unknown skill id {job.SkillId}.");
        }

        var nJob = new Job
        {
            Capacity = job.Capacity,
            Description = job.Description,
            ExternalReference = job.ExternalReference,
            Latitude = job.Latitude,
            Longitude = job.Longitude,
            Name = job.Name,
            Priority = job.Priority,
            Skill = skill,
            ServiceTimeSeconds = job.ServiceTimeSeconds
        };

        _context.Jobs.Add(nJob);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetJob), new { id = nJob.Id }, nJob.ToResponse());
    }

    /// <summary>Aktualisiert einen bestehenden Sammelpunkt.</summary>
    /// <response code="200">Der aktualisierte Sammelpunkt.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(JobResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<ActionResult<JobResponse>> UpdateJob(int id, [FromBody] JobDTO job, CancellationToken ct)
    {
        var existingJob = await _context.Jobs
            .Include(j => j.Skill)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (existingJob == null)
        {
            return NotFound();
        }

        var skill = await _context.Skills.FirstOrDefaultAsync(s => s.Id == job.SkillId, ct);
        if (skill == null)
        {
            return ValidationProblem($"Unknown skill id {job.SkillId}.");
        }

        // Ausdrückliche Zuweisung Feld für Feld. Ein CurrentValues.SetValues(dto) würde still
        // jedes künftig ergänzte DTO-Feld mitschreiben, auch ein unbeabsichtigtes.
        existingJob.Name = job.Name;
        existingJob.Description = job.Description;
        existingJob.ExternalReference = job.ExternalReference;
        existingJob.Capacity = job.Capacity;
        existingJob.Priority = job.Priority;
        existingJob.Latitude = job.Latitude;
        existingJob.Longitude = job.Longitude;
        existingJob.Skill = skill;
        existingJob.ServiceTimeSeconds = job.ServiceTimeSeconds;

        await _context.SaveChangesAsync(ct);
        return Ok(existingJob.ToResponse());
    }

    /// <summary>Löscht einen Sammelpunkt.</summary>
    /// <remarks>
    /// Halte bereits gefahrener Routen bleiben erhalten; ihr Verweis auf diesen Sammelpunkt wird
    /// auf <c>null</c> gesetzt. Eine Route ist ein Nachweis und darf nicht rückwirkend
    /// verschwinden, wenn ein Container aus den Stammdaten entfernt wird.
    /// </remarks>
    /// <response code="204">Erfolgreich gelöscht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<IActionResult> DeleteJob(int id, CancellationToken ct)
    {
        var job = await _context.Jobs.FindAsync(new object[] { id }, ct);
        if (job == null)
        {
            return NotFound();
        }

        _context.Jobs.Remove(job);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Ruft ausschließlich die clientseitigen Rohdaten eines Sammelpunkts ab.
    /// </summary>
    /// <remarks>
    /// Diese Rohdaten sind bereits Teil der normalen Sammelpunkt-Antwort
    /// (<see cref="JobResponse.Data"/>). Dieser Endpunkt existiert zusätzlich für Aufrufer, die
    /// ausschließlich dieses Feld benötigen, ohne den restlichen Sammelpunkt (inkl. Fraktion und
    /// Kapazität) mitzuladen – etwa ein Client, der lediglich periodisch auf Änderungen prüft.
    /// </remarks>
    /// <param name="id">Die ID des Sammelpunkts.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <response code="200">Die Rohdaten des Sammelpunkts. <c>Data</c> ist <c>null</c>, wenn noch keine gesetzt wurden.</response>
    /// <response code="404">Kein Sammelpunkt mit dieser ID.</response>
    [HttpGet("{id:int}/data")]
    [ProducesResponseType(typeof(JobDataResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<JobDataResponse>> GetJobData(int id, CancellationToken ct)
    {
        // Projektion statt vollständiger Entität: Für dieses schlanke Endpunkt wird weder die
        // Fraktion (Include) noch irgendein anderes Feld des Sammelpunkts benötigt.
        var data = await _context.Jobs.AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new JobDataResponse(j.Id, j.Data))
            .FirstOrDefaultAsync(ct);

        return data is null ? NotFound() : Ok(data);
    }

    /// <summary>
    /// Setzt (ersetzt vollständig) die clientseitigen Rohdaten eines Sammelpunkts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Der Server interpretiert diesen Wert an keiner Stelle</b> und validiert ihn nicht als
    /// JSON. Er wird unverändert gespeichert und über <see cref="JobResponse.Data"/> bzw.
    /// <see cref="GetJobData"/> unverändert zurückgegeben – reiner Transport zwischen Clients:
    /// Ein Aufrufer kann hier Daten ablegen (z. B. Sensormesswerte, eine Konfiguration), ein
    /// anderer kann darauf reagieren.
    /// </para>
    /// <para>
    /// Diese Rohdaten sind ausdrücklich vom übrigen Sammelpunkt-Datensatz getrennt: Anlegen
    /// (<c>POST /api/Jobs</c>) und Aktualisieren (<c>PUT /api/Jobs/{id}</c>) fassen dieses Feld
    /// nicht an, und dieser Endpunkt fasst kein anderes Feld des Sammelpunkts an. Ein <c>null</c>
    /// oder leerer Wert löscht vorhandene Rohdaten wieder (Vorgabe ist <c>null</c>).
    /// </para>
    /// </remarks>
    /// <param name="id">Die ID des Sammelpunkts.</param>
    /// <param name="data">Die zu speichernden Rohdaten.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <response code="200">Die aktualisierten Rohdaten des Sammelpunkts.</response>
    /// <response code="400">Ungültige Eingabedaten (z. B. länger als 16.000 Zeichen).</response>
    /// <response code="404">Kein Sammelpunkt mit dieser ID.</response>
    [HttpPost("{id:int}/data")]
    [ProducesResponseType(typeof(JobDataResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(403)]
    [CheckApplicationPermission(ApplicationRights.EditJobs)]
    public async Task<ActionResult<JobDataResponse>> SetJobData(
        int id, [FromBody] JobDataDTO data, CancellationToken ct)
    {
        var job = await _context.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null)
        {
            return NotFound();
        }

        // Leerer String und null werden bewusst gleich behandelt (siehe Klassendokumentation):
        // beide bedeuten "keine Rohdaten". Eine Normalisierung ist trotzdem unnötig, da für
        // dieses Feld ohnehin keine serverseitige Bedeutung besteht - der Wert wird 1:1 übernommen.
        job.Data = data.Data;
        await _context.SaveChangesAsync(ct);

        return Ok(job.ToDataResponse());
    }
}
