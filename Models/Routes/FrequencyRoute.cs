using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using SmartRoutingAPI.Models.Vehicles;
using Microsoft.EntityFrameworkCore.ValueGeneration.Internal;
using SmartRoutingAPI.Models.Base;

namespace SmartRoutingAPI.Models.Routes;

[Description("This table defines so called frequency routes. It's an easy way of defining (endlessly) recurring routes. Routes have to generated on the fly.")]
public class FrequencyRoute : BaseData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, Description("Start date of when the recurring route starts. This is the reference to the generated routes. If you want to make a bi-weekly Monday route, you'll need to set this date to a Monday.")]
    public DateTimeOffset StartDate { get; set; }

    [Required, Description("End date of when the recurring route generation ends. This is the (time) reference to the generated routes. If you want your generated routs to end at 4pm, you need to set this time to the end-date.")]
    public DateTimeOffset EndDate { get; set; }

    [Required, Description("The frequency of the route (represented as a TimeSpan).")]
    public TimeSpan Frequency { get; set; }

    [Required, Description("The default Skills for that route. They are used for the resulting route, but can be changed on it later.")]
    public virtual ICollection<Skill> Skills { get; set; } = null!;

    [Required, Description("The default color of the routes generated from the frequency route. Color is a RGBA color (0-FF R,G,B and A)")]
    public uint Color { get; set; }

    [Required, Description("Das Fahrzeug, welches der Route zugewiesen ist.")]
    virtual public Vehicle Vehicle { get; set; } = null!;


    [Required, Description("Die Start-Breitengradkoordinate der Route. Erforderliche Angabe.")]
    public double StartLatitude { get; set; }

    [Required, Description("Die Start-Längengradkoordinate der Route. Erforderliche Angabe.")]
    public double StartLongitude { get; set; }

    [Required, Description("Die Ziel-Breitengradkoordinate der Route. Erforderliche Angabe.")]
    public double EndLatitude { get; set; }

    [Required, Description("Die Ziel-Längengradkoordinate der Route. Erforderliche Angabe.")]
    public double EndLongitude { get; set; }

    /// <summary>
    /// Vorgabe für die aus dieser Frequenzroute erzeugten Routen: Soll die Schicht bewusst
    /// ausgefüllt werden?
    /// </summary>
    /// <remarks>
    /// Wird beim Erzeugen einer konkreten Route auf <see cref="Route.FillShift"/> übertragen und
    /// kann dort weiterhin einzeln geändert werden.
    /// </remarks>
    [Required, Description(
        "Vorgabe für erzeugte Routen: true füllt die Schicht möglichst vollständig aus, "
        + "false (Vorgabe) plant ausschließlich nach Emissionseffizienz.")]
    public bool FillShift { get; set; }
}
