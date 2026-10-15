using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartRoutingAPI.Models.Base;

namespace SmartRoutingAPI.Models.Vehicles;

/// <summary>
/// Ein Sammelfahrzeug. Bestimmt über <see cref="Capacity"/> die Nutzlast einer Ladung und über
/// <see cref="Skills"/>, welche Fraktionen es fahren darf.
/// </summary>
public class Vehicle : BaseData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Nutzlast des Fahrzeugs in Kapazitätseinheiten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gleiche Einheit wie <see cref="Jobs.Job.Capacity"/> und
    /// <see cref="Jobs.DeliveryDestination.MaximumCapacity"/>. Die Einheit ist systemweit frei
    /// gewählt; die Umrechnung in Kilogramm für die CO2e-Bilanz erfolgt ausschließlich über
    /// <c>Routing:Emissions:PayloadUnitKilograms</c>.
    /// </para>
    /// <para>
    /// <b>Typänderung:</b> Dieses Feld war <c>long</c>, während die Sammelmenge eines Punkts
    /// <c>double</c> ist. Zwei Zahlentypen für dieselbe physikalische Größe sind eine
    /// Fehlerquelle: Eine Nutzlast von 12,5 Einheiten war nicht darstellbar, und der Vergleich
    /// „passt die Menge noch?“ lief über eine stillschweigende Typerweiterung. Jetzt ist beides
    /// <c>double</c>.
    /// </para>
    /// </remarks>
    [Required, Description(
        "Nutzlast in Kapazitätseinheiten. Gleiche Einheit wie Job.Capacity.")]
    [Range(0.0, 1_000_000.0, ErrorMessage = "Die Kapazität muss zwischen 0 und 1.000.000 liegen.")]
    public double Capacity { get; set; }

    /// <summary>Die Fraktionen, für die dieses Fahrzeug eingesetzt werden darf (n:m).</summary>
    [Description("Alle Fraktionen, die dieses Fahrzeug sammeln darf.")]
    public virtual ICollection<Skill> Skills { get; set; } = new List<Skill>();
}
