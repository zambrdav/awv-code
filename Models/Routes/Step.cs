using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using SmartRoutingAPI.Models.Jobs;

namespace SmartRoutingAPI.Models.Routes;

/// <summary>
/// Die Art eines Halts einer berechneten Route.
/// </summary>
/// <remarks>
/// <para>
/// Die Werte entsprechen eins zu eins
/// <see cref="SmartRoutingAPI.Services.Routing.PlannedStopKind"/>, damit zwischen Planung und
/// Persistenz keine Übersetzungstabelle nötig ist, die auseinanderlaufen kann.
/// </para>
/// <para>
/// <b>Vorher:</b> Die Aufzählung trug VROOM-Vokabular (<c>Pickup</c>, <c>Delivery</c>) sowie zwei
/// Werte, die kein Codepfad je erzeugen konnte (<c>Job</c>, <c>Break</c>). <c>Pickup</c> und
/// <c>Delivery</c> waren zudem irreführend: „Delivery“ ist keine Auslieferung an einen Kunden,
/// sondern die Entleerung des Fahrzeugs an einer Abladestelle.
/// </para>
/// </remarks>
public enum StepType
{
    /// <summary>Schichtbeginn am Startpunkt.</summary>
    Start = 0,

    /// <summary>Ein Sammelpunkt wird geleert (Material wird aufgenommen).</summary>
    Collection = 1,

    /// <summary>Das Fahrzeug wird an einer Abladestelle entleert.</summary>
    Disposal = 2,

    /// <summary>Schichtende am Zielpunkt.</summary>
    End = 3
}

/// <summary>
/// Ein einzelner Halt einer berechneten Route, in Fahrtreihenfolge.
/// </summary>
/// <remarks>
/// <para>
/// Die Schrittliste ist die <b>maßgebliche</b> Darstellung einer Route: Sie allein enthält die
/// Reihenfolge, die Entleerungen und die Zeitpunkte. <c>Route.Jobs</c> ist lediglich die
/// (ungeordnete) Menge der bedienten Sammelpunkte und dient der Prüfung auf Doppelbelegung.
/// </para>
/// <para>
/// <b>Entfallene Felder.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>VroomId</c> – Kennung des abgelösten Optimierers. Ersetzt durch die fachlichen
///     Fremdschlüssel <see cref="JobId"/> bzw. <see cref="DeliveryDestinationId"/>, mit denen ein
///     Halt erstmals eindeutig auf seinen Stammdatensatz verweist.
///   </description></item>
///   <item><description>
///     <c>WaitingTime</c> – wurde ausnahmslos auf <c>TimeSpan.Zero</c> gesetzt. Ein Feld, das nur
///     einen Wert annehmen kann, trägt keine Information und verleitet zu der Annahme, es gäbe
///     eine Wartezeitrechnung.
///   </description></item>
///   <item><description>
///     <c>Violations</c> – Nebenbedingungsverletzungen des VROOM-Modells. Kein Codepfad hat je
///     eine Verletzung erzeugt; die Liste war in jeder Antwort leer.
///   </description></item>
///   <item><description>
///     <c>LoadJson</c> – ein <c>string</c>, der ein JSON-Array mit stets genau einem Element
///     enthielt. Ersetzt durch <see cref="LoadUnitsAfter"/> als echte Zahl.
///   </description></item>
/// </list>
/// </remarks>
public class Step
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int RouteId { get; set; }

    [JsonIgnore]
    public virtual Route? Route { get; set; }

    /// <summary>
    /// Position dieses Halts in der Fahrtreihenfolge, beginnend bei 0 (Schichtstart).
    /// </summary>
    /// <remarks>
    /// Zuvor ergab sich die Reihenfolge allein aus der Einfügereihenfolge bzw. der
    /// Primärschlüssel-Sortierung. Das ist keine Zusage, die eine Datenbank einhalten muss: Ohne
    /// <c>ORDER BY</c> ist die Zeilenfolge unbestimmt. Eine falsch sortierte Schrittliste ist für
    /// den Fahrer nicht als Fehler erkennbar, deshalb wird die Reihenfolge nun ausdrücklich
    /// gespeichert.
    /// </remarks>
    [Required, Description("Position in der Fahrtreihenfolge, beginnend bei 0.")]
    public int Sequence { get; set; }

    [Required, Description("Art des Halts.")]
    public StepType StepType { get; set; }

    /// <summary>Der Sammelpunkt dieses Halts; nur bei <see cref="StepType.Collection"/> gesetzt.</summary>
    public int? JobId { get; set; }

    /// <summary>Der bediente Sammelpunkt. <c>null</c>, wenn der Stammdatensatz gelöscht wurde.</summary>
    [JsonIgnore]
    public virtual Job? Job { get; set; }

    /// <summary>Die Abladestelle dieses Halts; nur bei <see cref="StepType.Disposal"/> gesetzt.</summary>
    public int? DeliveryDestinationId { get; set; }

    /// <summary>Die genutzte Abladestelle. <c>null</c>, wenn der Stammdatensatz gelöscht wurde.</summary>
    [JsonIgnore]
    public virtual DeliveryDestination? DeliveryDestination { get; set; }

    [Required, Description("Geplanter Ankunftszeitpunkt an diesem Halt.")]
    public DateTimeOffset Arrival { get; set; }

    /// <summary>
    /// Aufsummierte <b>reine Fahrzeit</b> vom Schichtstart bis zu diesem Halt, ohne Bedienzeiten.
    /// </summary>
    /// <remarks>
    /// Hieß zuvor <c>Duration</c>. Der Name legte die Dauer <em>dieses</em> Halts nahe, gespeichert
    /// war aber die kumulierte Fahrzeit seit Schichtbeginn – eine Verwechslung, die jede
    /// clientseitige Auswertung still verfälscht.
    /// </remarks>
    [Required, Description("Aufsummierte reine Fahrzeit vom Schichtstart bis zu diesem Halt.")]
    public TimeSpan TravelTimeFromStart { get; set; }

    /// <summary>Bedienzeit an diesem Halt.</summary>
    [Required, Description("Bedienzeit an diesem Halt.")]
    public TimeSpan ServiceTime { get; set; }

    /// <summary>
    /// Aufsummierte Fahrstrecke vom Schichtstart bis zu diesem Halt in Metern.
    /// </summary>
    /// <remarks>Hieß zuvor <c>Distance</c> – ohne Einheit und ohne Bezugspunkt im Namen.</remarks>
    [Required, Description("Aufsummierte Fahrstrecke vom Schichtstart bis zu diesem Halt in Metern.")]
    public int DistanceFromStartMeters { get; set; }

    /// <summary>
    /// Ladung auf dem Fahrzeug <em>nach</em> Abschluss dieses Halts, in echten
    /// Kapazitätseinheiten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dies ist die Ladung, die auf dem folgenden Abschnitt transportiert wird, und damit die
    /// Grundlage der lastabhängigen CO2e-Bilanz. An einer Abladestelle ist der Wert 0.
    /// </para>
    /// <para>
    /// Der Wert ist <b>nicht skaliert</b>: 1,2 bedeutet 1,2 Einheiten. Die Ganzzahl-Skalierung, die
    /// ein ganzzahliger Optimierer benötigt, bleibt vollständig in
    /// <c>OrToolsCollectionPlanner</c>.
    /// </para>
    /// </remarks>
    [Required, Description("Ladung nach diesem Halt in Kapazitätseinheiten (nicht skaliert).")]
    public double LoadUnitsAfter { get; set; }

    /// <summary>Anzeigename des Halts (Name des Sammelpunkts bzw. der Abladestelle).</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    [Required]
    [Range(-90.0, 90.0)]
    public double Latitude { get; set; }

    [Required]
    [Range(-180.0, 180.0)]
    public double Longitude { get; set; }
}
