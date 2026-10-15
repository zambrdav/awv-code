using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Contracts;

/// <summary>
/// Standardisierte Query-Parameter für Seiten-basierte (paginierte) Listen-Endpunkte.
/// Verhindert unbegrenzte Ergebnismengen (§4.3 des Remediation-Plans).
/// </summary>
public sealed class PageQuery
{
    /// <summary>Obergrenze der Seitengröße. Schützt vor absichtlich riesigen Anfragen.</summary>
    public const int MaxPageSize = 200;

    private int _page = 1;
    private int _pageSize = 50;

    /// <summary>1-basierte Seitennummer. Werte &lt; 1 werden auf 1 normalisiert.</summary>
    [Description("1-basierte Seitennummer (Standard: 1).")]
    public int Page
    {
        get => _page;
        init => _page = value < 1 ? 1 : value;
    }

    /// <summary>Elemente pro Seite. Wird hart auf [1, 200] begrenzt.</summary>
    [Description("Elemente pro Seite (Standard: 50, Maximum: 200).")]
    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = Math.Clamp(value, 1, MaxPageSize);
    }

    /// <summary>Anzahl der zu überspringenden Elemente (für <c>Skip</c> in EF-Abfragen).</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>Anzahl der zu nehmenden Elemente (für <c>Take</c> in EF-Abfragen).</summary>
    public int Take => PageSize;
}

/// <summary>
/// Einheitliche Hülle für paginierte Antworten. Der gesamte Payload-Vertrag der Listen-Endpunkte.
/// </summary>
/// <typeparam name="T">Der Typ der Elemente (immer ein Response-DTO, niemals eine EF-Entität).</typeparam>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    /// <summary>Gesamtzahl der Seiten für die aktuelle Seitengröße.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Gibt an, ob eine weitere Seite existiert.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>Gibt an, ob eine vorherige Seite existiert.</summary>
    public bool HasPreviousPage => Page > 1;
}

/// <summary>Der Status eines asynchronen Optimierungs-Auftrags.</summary>
public enum OptimizationStatus
{
    /// <summary>In der Warteschlange, noch nicht gestartet.</summary>
    Queued = 0,

    /// <summary>Wird gerade berechnet.</summary>
    Running = 1,

    /// <summary>Erfolgreich abgeschlossen; <see cref="OptimizationJobResponse.Result"/> ist gesetzt.</summary>
    Succeeded = 2,

    /// <summary>Fehlgeschlagen; <see cref="OptimizationJobResponse.Error"/> ist gesetzt.</summary>
    Failed = 3
}

/// <summary>
/// Rückgabe für eingereihte Routen-Optimierungen. Schreib-Endpunkte antworten mit
/// <c>202 Accepted</c> und dieser Hülle; der Client pollt anschließend
/// <c>GET /api/Routes/jobs/{jobId}</c>, bis <see cref="Status"/> terminal ist (§4.4).
/// </summary>
public sealed record OptimizationJobResponse(
    Guid JobId,
    OptimizationStatus Status,
    RouteResponse? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
