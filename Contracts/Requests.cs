using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Contracts;

// =============================================================================================
// Route-related request DTOs.
//
// Centralised here (previously nested inside RoutesController) so the controllers and the
// RouteOptimizationService share one definition. Validation is declarative + IValidatableObject,
// so [ApiController] returns 400 ValidationProblemDetails automatically instead of throwing
// NullReferenceExceptions (§3.3 ST1 / §4.6).
// =============================================================================================

/// <summary>Payload zum Erstellen/Berechnen einer konkreten Route.</summary>
public class RouteDTO : IValidatableObject
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string Description { get; set; } = "";

    [MaxLength(512)]
    public string ExternalReference { get; set; } = "";

    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }

    /// <summary>Die IDs der Skills dieser Route. Muss mindestens einen Eintrag enthalten.</summary>
    [Required, MinLength(1, ErrorMessage = "Es muss mindestens ein Skill angegeben werden.")]
    public IList<int> SkillsIdList { get; set; } = new List<int>();

    [Description("RGBA-Farbe (0xRRGGBBAA).")]
    public uint Color { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "VehicleId muss positiv sein.")]
    public int VehicleId { get; set; }

    public double StartLatitude { get; set; }
    public double StartLongitude { get; set; }
    public double EndLatitude { get; set; }
    public double EndLongitude { get; set; }

    /// <summary>
    /// Wenn <c>true</c>, wird die Schicht bewusst möglichst vollständig ausgefüllt: Nach einer
    /// Zwischenentleerung fährt das Fahrzeug weiter und sammelt, solange das Schichtfenster reicht.
    /// Wenn <c>false</c> (Vorgabe), wird ein Sammelpunkt nur angefahren, wenn sein Nutzen die
    /// dadurch zusätzlich verursachten Emissionen übersteigt.
    /// </summary>
    /// <remarks>
    /// In beiden Fällen ist die Reihenfolge der Halte emissionsoptimal; das Kennzeichen verändert
    /// nur, wie viele Punkte aufgenommen werden.
    /// </remarks>
    [Description(
        "true füllt die Schicht möglichst vollständig aus (Weiterfahrt nach dem Abladen). "
        + "false (Vorgabe) plant ausschließlich nach Emissionseffizienz.")]
    public bool FillShift { get; set; }

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EndDate <= StartDate)
        {
            yield return new ValidationResult(
                "EndDate muss nach StartDate liegen.", new[] { nameof(EndDate) });
        }
    }
}

/// <summary>Payload zum Erstellen/Aktualisieren einer wiederholenden (Frequenz-)Route.</summary>
public class RecurringRouteDTO : RouteDTO
{
    [Description("Der Wiederholungsabstand (TimeSpan).")]
    public TimeSpan Frequency { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (Frequency <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "Frequency muss größer als 0 sein.", new[] { nameof(Frequency) });
        }
    }
}

/// <summary>Payload zum Ändern der Metadaten einer Route (mit anschließender Neuberechnung).</summary>
public class RouteMetadataDTO : IValidatableObject
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string? Description { get; set; }

    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }
    public double StartLatitude { get; set; }
    public double StartLongitude { get; set; }
    public double EndLatitude { get; set; }
    public double EndLongitude { get; set; }

    [Range(1, int.MaxValue)]
    public int VehicleId { get; set; }

    [Required, MinLength(1)]
    public List<int> SkillsIdList { get; set; } = new();

    /// <summary>
    /// Wenn <c>true</c>, wird die Schicht bewusst möglichst vollständig ausgefüllt. Wenn
    /// <c>false</c> (Vorgabe), wird ausschließlich nach Emissionseffizienz geplant.
    /// </summary>
    [Description(
        "true füllt die Schicht möglichst vollständig aus (Weiterfahrt nach dem Abladen). "
        + "false (Vorgabe) plant ausschließlich nach Emissionseffizienz.")]
    public bool FillShift { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EndDate <= StartDate)
        {
            yield return new ValidationResult(
                "EndDate muss nach StartDate liegen.", new[] { nameof(EndDate) });
        }
    }
}

/// <summary>
/// Typisierter Body zum Hinzufügen/Entfernen von Jobs einer Route. Ersetzt die zuvor als
/// komma-getrennte Query-Strings übergebenen IDs (§3.5 B5 / §4.7).
/// </summary>
public sealed class RouteJobPatch
{
    public IReadOnlyList<int> AddJobIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> RemoveJobIds { get; set; } = Array.Empty<int>();
}
