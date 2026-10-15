using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using SmartRoutingAPI.Models.Base;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Models.Jobs;

namespace SmartRoutingAPI.Models.Vehicles;

/// <summary>
/// Eine <b>Fähigkeit</b> bzw. Fraktion – in der Glassammlung z. B. Weißglas, Grünglas, Braunglas.
/// </summary>
/// <remarks>
/// Die Fähigkeit ist die zentrale Zuordnungsgröße des Systems. Ein Sammelpunkt kommt für eine
/// Route nur in Frage, wenn seine Fähigkeit sowohl der Route als auch dem Fahrzeug zugeordnet ist,
/// und eine Abladestelle kommt nur in Frage, wenn sie die Fraktion annimmt (oder gar keine
/// Einschränkung hat).
/// </remarks>
[Description("Fraktionen bzw. Fähigkeiten, über die Fahrzeuge, Routen, Sammelpunkte und Abladestellen zugeordnet werden.")]
public class Skill : BaseData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Die Fahrzeuge, die diese Fraktion fahren können (n:m).</summary>
    public virtual ICollection<Vehicle> Vehicles { get; set; } = new List<Vehicle>();

    /// <summary>Die Routen, die diese Fraktion sammeln (n:m).</summary>
    public virtual ICollection<Routes.Route> Routes { get; set; } = new List<Routes.Route>();

    /// <summary>Die wiederholenden Routen, die diese Fraktion sammeln (n:m).</summary>
    public virtual ICollection<FrequencyRoute> FrequencyRoutes { get; set; } = new List<FrequencyRoute>();

    /// <summary>Die Sammelpunkte dieser Fraktion (1:n – ein Sammelpunkt hat genau eine Fraktion).</summary>
    public virtual ICollection<Job> Jobs { get; set; } = new List<Job>();

    /// <summary>
    /// Die Abladestellen, die diese Fraktion annehmen (n:m).
    /// </summary>
    /// <remarks>
    /// Diese Gegen-Navigation ist zwingend erforderlich: Ohne sie leitete EF Core aus
    /// <see cref="DeliveryDestination.Skills"/> eine 1:n-Beziehung ab und legte den
    /// Fremdschlüssel <c>Skills.DeliveryDestinationId</c> an – eine Fraktion konnte dann nur an
    /// einer einzigen Abladestelle angenommen werden. Mit beiden Navigationen entsteht die
    /// korrekte Verknüpfungstabelle <c>DeliveryDestinationSkill</c>.
    /// </remarks>
    public virtual ICollection<DeliveryDestination> DeliveryDestinations { get; set; } = new List<DeliveryDestination>();
}
