using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartRoutingAPI.Models.Base;
using SmartRoutingAPI.Models.Vehicles;

namespace SmartRoutingAPI.Models.Jobs;

/// <summary>
/// Eine <b>Abladestelle</b>: der Ort, an dem das Fahrzeug entleert wird (Glaswerk, Umschlagplatz,
/// Betriebshof).
/// </summary>
/// <remarks>
/// <para>
/// Dies ist das <b>einzige</b> Ablieferungskonzept des Systems. Die frühere, zusätzliche Tabelle
/// <c>Deliveries</c> (Typ <c>DeliveryJob</c>) mit einem festen Ablieferungsort je Sammelpunkt ist
/// entfernt.
/// </para>
/// <para>
/// Abladestellen sind <em>Alternativen</em>: Der Optimierer entscheidet je Fahrzeugladung, welche
/// Stelle angefahren wird, und darf innerhalb einer Schicht mehrfach und an verschiedenen Stellen
/// entleeren. Genau diese Fähigkeit war der Grund für den Wechsel von VROOM zu OR-Tools.
/// </para>
/// </remarks>
public class DeliveryDestination : BaseData, IGPS_Point
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, Description("Die Breitengradkoordinate der Abladestelle. Erforderliche Angabe.")]
    [Range(-90.0, 90.0, ErrorMessage = "Der Breitengrad muss zwischen -90 und 90 liegen.")]
    public double Latitude { get; set; }

    [Required, Description("Die Längengradkoordinate der Abladestelle. Erforderliche Angabe.")]
    [Range(-180.0, 180.0, ErrorMessage = "Der Längengrad muss zwischen -180 und 180 liegen.")]
    public double Longitude { get; set; }

    /// <summary>
    /// Aufnahmefähigkeit in Kapazitätseinheiten. Werte kleiner oder gleich 0 bedeuten unbegrenzt.
    /// </summary>
    /// <remarks>
    /// Begrenzt, wie viele Fahrzeugladungen diese Stelle je Schicht annehmen kann
    /// (<c>floor(MaximumCapacity / Fahrzeugkapazität)</c>). Gleiche Einheit wie
    /// <see cref="Job.Capacity"/>.
    /// </remarks>
    [Required, Description(
        "Aufnahmefähigkeit in Kapazitätseinheiten. Werte <= 0 bedeuten unbegrenzt.")]
    public double MaximumCapacity { get; set; }

    /// <summary>
    /// Die Fraktionen, die diese Abladestelle annimmt. Eine <b>leere</b> Liste bedeutet
    /// „nimmt alles an“.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dies ist eine echte n:m-Beziehung: Dieselbe Fraktion darf an mehreren Abladestellen
    /// angenommen werden, und eine Abladestelle darf mehrere Fraktionen annehmen.
    /// </para>
    /// <para>
    /// <b>Vorher fehlerhaft:</b> Ohne die Gegen-Navigation
    /// <see cref="Skill.DeliveryDestinations"/> leitete EF Core aus dieser Sammlung eine
    /// 1:n-Beziehung ab und legte den Fremdschlüssel <c>Skills.DeliveryDestinationId</c> an. Eine
    /// Fraktion konnte damit nur <em>einer</em> Abladestelle zugeordnet sein: „Weißglas“ an
    /// Standort B zuzuordnen entfernte es stillschweigend von Standort A, und das
    /// <c>Skills.Clear()</c> beim Aktualisieren löste die Zuordnung global. Weil der Planer
    /// Abladestellen ohne Fraktion als universell nutzbar behandelt, blieb der Fehler unsichtbar
    /// und erzeugte lediglich falsche Pläne.
    /// </para>
    /// </remarks>
    [Description("Die angenommenen Fraktionen. Leere Liste bedeutet: nimmt alle Fraktionen an.")]
    public virtual ICollection<Skill> Skills { get; set; } = new List<Skill>();

    /// <summary>
    /// Feste Bedienzeit einer Entleerung an dieser Abladestelle in Sekunden (Anmelden, Wiegen,
    /// Rangieren, Abwiegen). <c>null</c> übernimmt die globale Vorgabe aus
    /// <c>Routing:ServiceTimes:FacilitySeconds</c>.
    /// </summary>
    /// <remarks>
    /// Dies ist der entscheidende Regler gegen unnötige Zwischenentleerungen: Je teurer eine
    /// Entleerung in Zeit ist, desto eher füllt der Optimierer das Fahrzeug aus, statt einen
    /// zusätzlichen Halt einzuplanen. Ein zu niedriger Wert lässt Entleerungen fast kostenlos
    /// erscheinen.
    /// </remarks>
    [Description(
        "Feste Bedienzeit einer Entleerung in Sekunden. Leer lassen, um die globale Vorgabe zu verwenden.")]
    [Range(0, 28800, ErrorMessage = "Die Bedienzeit muss zwischen 0 und 28800 Sekunden liegen.")]
    public int? ServiceTimeSeconds { get; set; }

    /// <summary>
    /// Zusätzliche Bedienzeit je abgeladener Kapazitätseinheit in Sekunden.
    /// </summary>
    /// <remarks>
    /// Damit lässt sich ein Modell wie „5 Minuten fest zuzüglich 6 Sekunden je m³“ abbilden. Für
    /// die Planung wird dabei mit einer vollen Fahrzeugladung gerechnet, weil Bogenkosten in einem
    /// Tourenoptimierer nicht vom aktuellen Ladezustand abhängen dürfen. Das überschätzt die
    /// Zeit einer letzten Teilentleerung und ist damit betrieblich auf der sicheren Seite.
    /// </remarks>
    [Description("Zusätzliche Bedienzeit je abgeladener Kapazitätseinheit in Sekunden.")]
    [Range(0, 3600, ErrorMessage = "Der Wert muss zwischen 0 und 3600 Sekunden liegen.")]
    public double? ServiceSecondsPerUnit { get; set; }
}