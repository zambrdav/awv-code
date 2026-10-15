using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Misc;
using SmartRoutingAPI.Services;

namespace SmartRoutingAPI.Controllers;

/// <summary>
/// Endpunkte für das Live-Map-Matching von GPS-Koordinaten gegen das Straßennetz der
/// Valhalla-Instanz (Meili). Es werden GPS-Spuren entgegengenommen und auf die tatsächlich
/// befahrenen Straßen "gesnappt".
/// </summary>
/// <remarks>
/// Authentifizierung ist zwingend: Der Endpunkt nimmt bis zu 5000 Spurpunkte je Anfrage an und
/// leitet sie an die Valhalla-Instanz weiter. Ohne <see cref="AuthorizeAttribute"/> stünde damit
/// eine unauthentifizierte Last-Amplifikation auf die Routing-Infrastruktur offen. Beide Attribute
/// waren auskommentiert.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
[CheckApplicationPermission(ApplicationRights.ViewRoutes)]
public class MapMatchingController : ControllerBase
{
    private readonly IValhallaService _valhalla;
    private readonly ILogger<MapMatchingController> _logger;

    /// <summary>Erzeugt den Controller mit dem Valhalla-Dienst und einem Logger.</summary>
    /// <param name="valhalla">Der Dienst für die Kommunikation mit der Valhalla-Instanz.</param>
    /// <param name="logger">Logger für serverseitige Diagnose.</param>
    public MapMatchingController(IValhallaService valhalla, ILogger<MapMatchingController> logger)
    {
        _valhalla = valhalla;
        _logger = logger;
    }

    /// <summary>
    /// Gleicht eine GPS-Spur live auf das Straßennetz ab und liefert die gesnappten Punkte.
    /// </summary>
    /// <remarks>
    /// Nutzt Valhalla-Meili (<c>trace_attributes</c>). Für jeden Eingabepunkt wird der zugehörige,
    /// auf die Straße gesnappte Punkt zurückgegeben (1:1-Zuordnung), zusätzlich die gesnappte
    /// Gesamtgeometrie als Encoded Polyline. Optional können Kantenattribute (Straßennamen, Tempo,
    /// Straßenklasse) sowie die dekodierte Geometrie angefordert werden.
    ///
    /// Empfehlung für echte GPS-Daten: <c>shapeMatch = map_snap</c> (Standard) verwenden und – falls
    /// vorhanden – pro Punkt einen Zeitstempel (<c>time</c>) mitliefern.
    /// </remarks>
    /// <param name="request">Die zu matchende GPS-Spur samt Optionen.</param>
    /// <param name="cancellationToken">Wird bei Client-Trennung automatisch ausgelöst.</param>
    /// <returns>Die gematchten Punkte und die gesnappte Geometrie.</returns>
    /// <response code="200">Map-Matching erfolgreich.</response>
    /// <response code="400">Ungültige Eingabe (z. B. zu wenige oder ungültige Punkte).</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="422">Die Spur konnte nicht auf das Straßennetz abgeglichen werden.</response>
    /// <response code="502">Die Valhalla-Instanz ist nicht erreichbar oder antwortete fehlerhaft.</response>
    /// <response code="504">Zeitüberschreitung bei der Anfrage an die Valhalla-Instanz.</response>
    [HttpPost("match")]
    [ProducesResponseType(typeof(MapMatchResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(422)]
    [ProducesResponseType(502)]
    [ProducesResponseType(504)]
    public async Task<IActionResult> Match([FromBody] MapMatchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var result = await _valhalla.TraceAttributesAsync(request, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (ValhallaException ex)
        {
            return StatusCode(ex.SuggestedStatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            // Client hat die Anfrage abgebrochen – 499 (Client Closed Request), kein Serverfehler.
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler im Map-Matching-Endpunkt 'match'.");
            return StatusCode(500, new { error = "Unerwarteter Fehler beim Map-Matching." });
        }
    }

    /// <summary>
    /// Gleicht eine GPS-Spur auf das Straßennetz ab und liefert die vollständige, gesnappte Route.
    /// </summary>
    /// <remarks>
    /// Nutzt Valhalla-Meili (<c>trace_route</c>) und liefert die gesnappte Route mit Gesamtlänge (km),
    /// geschätzter Dauer (s) und den Teilgeometrien (Encoded Polyline je Leg). Optional wird die
    /// Geometrie serverseitig zu Koordinaten dekodiert.
    /// </remarks>
    /// <param name="request">Die zu matchende GPS-Spur samt Optionen.</param>
    /// <param name="cancellationToken">Wird bei Client-Trennung automatisch ausgelöst.</param>
    /// <returns>Die gesnappte Route inklusive Kennzahlen und Geometrie.</returns>
    /// <response code="200">Map-Matching erfolgreich.</response>
    /// <response code="400">Ungültige Eingabe (z. B. zu wenige oder ungültige Punkte).</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="422">Die Spur konnte nicht auf das Straßennetz abgeglichen werden.</response>
    /// <response code="502">Die Valhalla-Instanz ist nicht erreichbar oder antwortete fehlerhaft.</response>
    /// <response code="504">Zeitüberschreitung bei der Anfrage an die Valhalla-Instanz.</response>
    [HttpPost("route")]
    [ProducesResponseType(typeof(MapRouteResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(422)]
    [ProducesResponseType(502)]
    [ProducesResponseType(504)]
    public async Task<IActionResult> Route([FromBody] MapMatchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var result = await _valhalla.TraceRouteAsync(request, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (ValhallaException ex)
        {
            return StatusCode(ex.SuggestedStatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler im Map-Matching-Endpunkt 'route'.");
            return StatusCode(500, new { error = "Unerwarteter Fehler beim Map-Matching." });
        }
    }

    /// <summary>
    /// Prüft, ob die konfigurierte Valhalla-Instanz erreichbar und einsatzbereit ist.
    /// </summary>
    /// <remarks>
    /// Nützlich zur Betriebsüberwachung und um sicherzustellen, dass die Map-Matching-Endpunkte
    /// verwendet werden können, bevor Daten gesendet werden.
    /// </remarks>
    /// <param name="cancellationToken">Wird bei Client-Trennung automatisch ausgelöst.</param>
    /// <returns>Ein Objekt, das die Verfügbarkeit der Valhalla-Instanz beschreibt.</returns>
    /// <response code="200">Die Valhalla-Instanz ist erreichbar.</response>
    /// <response code="403">Zugriff verweigert (fehlende Berechtigung).</response>
    /// <response code="503">Die Valhalla-Instanz ist derzeit nicht erreichbar.</response>
    [HttpGet("status")]
    [ProducesResponseType(200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        try
        {
            var available = await _valhalla.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

            if (available)
            {
                return Ok(new { available = true });
            }

            return StatusCode(503, new { available = false, error = "Die Valhalla-Instanz ist derzeit nicht erreichbar." });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei der Valhalla-Statusprüfung.");
            return StatusCode(503, new { available = false, error = "Statusprüfung fehlgeschlagen." });
        }
    }
}
