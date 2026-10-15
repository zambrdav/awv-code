using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartRoutingAPI.Models.Base;
using SmartRoutingAPI.Models.Vehicles;

namespace SmartRoutingAPI.Models.Jobs;

/// <summary>
/// Ein <b>Sammelpunkt</b>: der Ort, an dem Material aufgenommen wird – in der Glassammlung ein
/// Glascontainer bzw. ein Glasplatz.
/// </summary>
/// <remarks>
/// <para>
/// Der Name „Job“ ist historisch (VROOM-Vokabular) und beschreibt fachlich einen Sammelpunkt.
/// Ein Sammelpunkt sagt <em>nichts</em> darüber aus, wohin das Material gebracht wird: Das
/// Abladen erfolgt an einer <see cref="DeliveryDestination"/>, und welche davon je Fahrzeugladung
/// genutzt wird, entscheidet der Optimierer.
/// </para>
/// <para>
/// <b>Entfallene Felder.</b> Zwei Felder wurden entfernt, weil sie das Datenmodell aktiv
/// verfälschten:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>DeliveryDestination</c> vom Typ <c>DeliveryJob</c> – ein fester Ablieferungsort je
///     Sammelpunkt. Er war die Notlösung für einen Optimierer, der keine Alternativstandorte
///     beherrschte. Der aktuelle Planer wählt die Abladestelle je Ladung emissionsoptimal; das
///     Feld hatte deshalb keinerlei Wirkung, trug aber denselben Namen wie der Typ
///     <see cref="DeliveryDestination"/> und war dadurch die Hauptquelle von Fehldeutungen.
///     Zusätzlich hinterließ jede Aktualisierung eines Sammelpunkts eine verwaiste Zeile in der
///     Tabelle <c>Deliveries</c>, für die es keinen Löschweg gab.
///   </description></item>
///   <item><description>
///     <c>AllowManualAssignment</c> – ein Kennzeichen ohne serverseitige Wirkung. Es wurde als
///     Migration „ManualPriorityField“ angelegt, als Berechtigung der Fahrer-Oberfläche
///     beschrieben und vom nächtlichen Import als „dieser Behälter hat keinen Füllstandssensor“
///     gefüllt – drei unvereinbare Bedeutungen in einer Spalte. Es wurde zudem beim Anlegen eines
///     Sammelpunkts nie gespeichert. Wird die Information „kein Sensor vorhanden“ benötigt, ist
///     sie unter diesem Namen neu einzuführen.
///   </description></item>
/// </list>
/// </remarks>
public class Job : BaseData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Erwartete Sammelmenge dieses Punkts in Kapazitätseinheiten.
    /// </summary>
    /// <remarks>
    /// Die Kapazitätseinheit ist systemweit frei gewählt (Kubikmeter, Kilogramm, Behälterzahl),
    /// aber <b>einheitlich</b>: Derselbe Maßstab gilt für <see cref="Vehicles.Vehicle.Capacity"/>
    /// und <see cref="DeliveryDestination.MaximumCapacity"/>. Die Umrechnung in Kilogramm für die
    /// CO2e-Bilanz erfolgt ausschließlich über <c>Routing:Emissions:PayloadUnitKilograms</c>.
    /// </remarks>
    [Required, Description(
        "Erwartete Sammelmenge in Kapazitätseinheiten. Gleiche Einheit wie Vehicle.Capacity.")]
    [Range(0.0, 1_000_000.0, ErrorMessage = "Die Sammelmenge muss zwischen 0 und 1.000.000 liegen.")]
    public double Capacity { get; set; }

    [Required, Description("Die Breitengradkoordinate des Sammelpunkts. Erforderliche Angabe.")]
    [Range(-90.0, 90.0, ErrorMessage = "Der Breitengrad muss zwischen -90 und 90 liegen.")]
    public double Latitude { get; set; }

    [Required, Description("Die Längengradkoordinate des Sammelpunkts. Erforderliche Angabe.")]
    [Range(-180.0, 180.0, ErrorMessage = "Der Längengrad muss zwischen -180 und 180 liegen.")]
    public double Longitude { get; set; }

    /// <summary>
    /// Priorität des Sammelpunkts (0–100). Steuert, wie teuer das Auslassen ist.
    /// </summary>
    /// <remarks>
    /// <b>Wichtig für die Datenpflege:</b> Die Priorität wirkt über einen Faktor
    /// <c>max(Routing:Planner:MinimumPriorityFactor, Priorität / 100)</c>. Mit der Vorgabe 0,25
    /// sind daher <b>alle Werte von 0 bis 25 wirkungsgleich</b>. Diese Untergrenze ist
    /// beabsichtigt – sie verhindert, dass neu angelegte Standorte mit Priorität 0 dauerhaft nie
    /// bedient werden. Wer feiner abstufen will, muss den Wertebereich oberhalb von 25 nutzen.
    /// </remarks>
    [Required, Description(
        "Priorität des Sammelpunkts (0-100). Achtung: Werte von 0 bis MinimumPriorityFactor*100 "
        + "(Vorgabe 25) sind wirkungsgleich.")]
    [Range(0, 100, ErrorMessage = "Die Priorität darf nur zwischen 0 und 100 (inklusive) betragen.")]
    public int Priority { get; set; }

    /// <summary>Die Routen, in denen dieser Sammelpunkt eingeplant ist (M:N).</summary>
    public virtual List<Routes.Route>? Routes { get; set; }

    /// <summary>
    /// Die Fähigkeit, die dieser Sammelpunkt erfordert (z. B. Weißglas).
    /// </summary>
    /// <remarks>
    /// Harter Filter: Ein Sammelpunkt kommt für eine Route nur in Frage, wenn seine Fähigkeit
    /// sowohl der Route <em>als auch</em> dem Fahrzeug zugeordnet ist. Ist die Schnittmenge leer,
    /// gibt es keine Kandidaten und die Routenberechnung schlägt fehl.
    /// </remarks>
    [Description("Die optionale Fähigkeit, die dieser Sammelpunkt erfordert.")]
    public virtual Skill? Skill { get; set; }

    /// <summary>
    /// Bedienzeit dieses Sammelpunkts in Sekunden. <c>null</c> übernimmt die globale Vorgabe aus
    /// <c>Routing:ServiceTimes:ContainerBaseSeconds</c>.
    /// </summary>
    /// <remarks>
    /// Ein Glasplatz mit vier Behältern und schwieriger Zufahrt braucht mehr Zeit als ein
    /// einzelner Behälter am Straßenrand. Weil die Bedienzeit in die Schichtdauer eingeht,
    /// verschiebt eine falsche Annahme die gesamte Tourenplanung – eine punktgenaue Angabe ist
    /// deshalb der wirksamste Hebel für realistische Pläne.
    /// </remarks>
    [Description(
        "Bedienzeit dieses Sammelpunkts in Sekunden. Leer lassen, um die globale Vorgabe zu verwenden.")]
    [Range(0, 7200, ErrorMessage = "Die Bedienzeit muss zwischen 0 und 7200 Sekunden liegen.")]
    public int? ServiceTimeSeconds { get; set; }

    /// <summary>
    /// Höchstlänge von <see cref="Data"/> in Zeichen.
    /// </summary>
    /// <remarks>
    /// Die Spalte ist als <c>TEXT</c> angelegt (MySQL-Obergrenze: 65.535 Byte, unabhängig vom
    /// Zeichensatz). Bei <c>utf8mb4</c> belegt ein Zeichen im ungünstigsten Fall 4 Byte; 16.000
    /// Zeichen sind daher mit höchstens 64.000 Byte garantiert innerhalb der Spaltengrenze, ganz
    /// unabhängig davon, welche Zeichen tatsächlich gesendet werden. Diese Prüfung erfolgt
    /// serverseitig, damit eine zu lange Eingabe als nachvollziehbarer 400-Fehler endet statt als
    /// unspezifischer Datenbankfehler beim Speichern.
    /// </remarks>
    public const int MaxDataLength = 16_000;

    /// <summary>
    /// Beliebige clientseitige Rohdaten dieses Sammelpunkts, üblicherweise (aber nicht zwingend)
    /// eine JSON-Zeichenkette.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Der Server interpretiert, validiert und verwendet diesen Wert an keiner Stelle.</b> Er
    /// wird unverändert gespeichert und zurückgegeben – reiner Transport zwischen Clients. Ein
    /// Aufrufer kann hier z. B. Sensordaten oder eine Konfiguration ablegen, ein anderer kann
    /// darauf reagieren. Weil serverseitig keine Bedeutung zugewiesen wird, ist bewusst
    /// <b>keine</b> JSON-Validierung vorgesehen: Ein reiner Text ohne JSON-Struktur ist ein
    /// gültiger Wert.
    /// </para>
    /// <para>
    /// Anlegen und Aktualisieren eines Sammelpunkts (<c>POST</c>/<c>PUT /api/Jobs</c>) lassen dieses
    /// Feld unangetastet. Gesetzt wird es ausschließlich über <c>POST /api/Jobs/{id}/data</c>,
    /// gelesen zusätzlich zum normalen Sammelpunkt-Abruf auch gezielt über
    /// <c>GET /api/Jobs/{id}/data</c>.
    /// </para>
    /// </remarks>
    [MaxLength(MaxDataLength), Description(
        "Beliebige clientseitige Rohdaten (üblicherweise JSON) dieses Sammelpunkts. Der Server "
        + "interpretiert den Inhalt nicht. Wird nicht über die normale Job-Erstellung/-Aktualisierung "
        + "gesetzt, sondern über POST /api/Jobs/{id}/data.")]
    public string? Data { get; set; }
}
