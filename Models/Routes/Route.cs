using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Drawing;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Base;
using SmartRoutingAPI.Models.Vehicles;

namespace SmartRoutingAPI.Models.Routes;

public class Route : BaseData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Die bedienten Sammelpunkte als <b>ungeordnete Menge</b> (n:m).
    /// </summary>
    /// <remarks>
    /// Diese Menge beantwortet ausschließlich die Frage „ist dieser Sammelpunkt schon verplant?“
    /// und wird für die Prüfung auf Doppelbelegung zeitlich überlappender Touren benötigt. Die
    /// <b>Reihenfolge</b> steht ausschließlich in <see cref="Steps"/>.
    /// </remarks>
    [Description("Die bedienten Sammelpunkte (ungeordnete Menge). Reihenfolge siehe Steps.")]
    virtual public IList<Job>? Jobs { get; set; }

    [Required, Description("Das Fahrzeug, welches der Route zugewiesen ist.")]
    virtual public Vehicle Vehicle { get; set; } = null!;

    [Required, Description("Die Fähigkeiten der Route.")]
    virtual public List<Skill> Skills { get; set; } = null!;

    [Required, Description("Farbe der Route. Wird als RGBA integer (0-FF für R,G,B und A in dieser Reihenfolge) interpretiert")]
    public uint Color { get; set; }

    [Required, Description("Zeit und Tag, wann diese Route startet (wird beim Erstellen gesetzt).")]
    public DateTimeOffset StartDate { get; set; }

    [Required, Description("Zeit und Tag, wann diese Route endet (wird beim Erstellen gesetzt).")]
    public DateTimeOffset EndDate { get; set; }


    [Required, Description("Die Start-Breitengradkoordinate der Route. Erforderliche Angabe.")]
    public double StartLatitude { get; set; }

    [Required, Description("Die Start-Längengradkoordinate der Route. Erforderliche Angabe.")]
    public double StartLongitude { get; set; }

    [Required, Description("Die Ziel-Breitengradkoordinate der Route. Erforderliche Angabe.")]
    public double EndLatitude { get; set; }

    [Required, Description("Die Ziel-Längengradkoordinate der Route. Erforderliche Angabe.")]
    public double EndLongitude { get; set; }

    [Description("Die Route in einer Polyline")]
    public string? Geometry { get; set; }

    [Description("Die gefahrenen Meter in dieser Route")]
    public int? Meters { get; set; }

    [Description("Summe aller Bedienzeiten der Route (alle Halte zusammen).")]
    public TimeSpan? Service { get; set; }

    [Description("Reine Fahrzeit der Route, ohne Bedienzeiten.")]
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Die Halte der Route in Fahrtreihenfolge – die maßgebliche Darstellung des Fahrauftrags.
    /// </summary>
    /// <remarks>
    /// Enthält Schichtstart, alle Sammelhalte, alle Entleerungen und das Schichtende. Die
    /// Reihenfolge ergibt sich aus <see cref="Step.Sequence"/> und nicht aus der Zeilenfolge der
    /// Datenbank.
    /// </remarks>
    [Description("Die Halte der Route in Fahrtreihenfolge (inkl. Start, Entleerungen und Ende).")]
    public virtual ICollection<Step>? Steps { get; set; }

    /// <summary>
    /// Steuert die Zielsetzung der Optimierung.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>false</c> (Vorgabe): Ein Sammelpunkt wird nur angefahren, wenn sein Nutzen die dadurch
    /// zusätzlich verursachten Emissionen übersteigt. Das ergibt die emissionsgünstigste Tour.
    /// </para>
    /// <para>
    /// <c>true</c>: Die Schicht wird bewusst ausgefüllt – es werden so viele Sammelpunkte bedient,
    /// wie im Schichtfenster unterzubringen sind, einschließlich Weiterfahrt nach einer
    /// Zwischenentleerung. Die Reihenfolge bleibt dabei emissionsoptimal.
    /// </para>
    /// </remarks>
    [Required, Description(
        "Wenn true, wird die Schicht möglichst vollständig ausgefüllt (Weiterfahrt nach dem Abladen). "
        + "Wenn false (Vorgabe), wird ausschließlich nach Emissionseffizienz geplant.")]
    public bool FillShift { get; set; }

    [Description("Die berechneten CO2e-Emissionen der Route in Gramm.")]
    public double? Co2eGrams { get; set; }

    [Description("Die eingesammelte Menge in Kapazitätseinheiten.")]
    public double? CollectedUnits { get; set; }

    [MaxLength(512), Description(
        "Quelle der verwendeten Emissionsfaktoren. Wird mitgespeichert, damit die CO2-Bilanz "
        + "einer Route nachträglich prüfbar bleibt.")]
    public string? EmissionFactorSource { get; set; }
}