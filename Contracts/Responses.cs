using SmartRoutingAPI.Models.Routes;

namespace SmartRoutingAPI.Contracts;

// =============================================================================================
// Antwort-DTOs — der VOLLSTÄNDIGE öffentliche Antwortvertrag der API.
//
// Diese Records enthalten keine Rück-Navigationen; der serialisierte Objektgraph ist damit
// bauartbedingt zyklenfrei. Controller geben niemals EF-Entitäten zurück, sondern ausschließlich
// diese Typen — abgebildet über die zentrale Stelle in Mapping.cs.
// =============================================================================================

/// <summary>Eine WGS84-Koordinate.</summary>
public sealed record GeoPointResponse(double Latitude, double Longitude);

/// <summary>Eine Fraktion bzw. Fähigkeit in flacher Form – ohne Rück-Navigationen.</summary>
/// <param name="Id">Kennung.</param>
/// <param name="Name">Anzeigename, z. B. „Weißglas“.</param>
/// <param name="Description">Freitext.</param>
/// <param name="ExternalReference">Kennung im Fremdsystem.</param>
public sealed record SkillResponse(int Id, string Name, string? Description, string? ExternalReference);

/// <summary>Ein Fahrzeug samt seiner Fraktionen.</summary>
/// <param name="Id">Kennung.</param>
/// <param name="Name">Anzeigename.</param>
/// <param name="Description">Freitext.</param>
/// <param name="ExternalReference">Kennung im Fremdsystem.</param>
/// <param name="Capacity">
/// Nutzlast in Kapazitätseinheiten. <b>Geändert von <c>long</c> auf <c>double</c></b>, damit
/// Fahrzeugnutzlast und Sammelmenge denselben Zahlentyp und dieselbe Einheit verwenden.
/// </param>
/// <param name="Skills">Die Fraktionen, die dieses Fahrzeug sammeln darf.</param>
public sealed record VehicleResponse(
    int Id, string Name, string? Description, string? ExternalReference,
    double Capacity, IReadOnlyList<SkillResponse> Skills);

/// <summary>
/// Ein Sammelpunkt (Glascontainer-Standort).
/// </summary>
/// <param name="Id">Kennung.</param>
/// <param name="Name">Anzeigename.</param>
/// <param name="Description">Freitext, üblicherweise Adresse und Zusatz.</param>
/// <param name="ExternalReference">
/// Kennung im Fremdsystem. Der nächtliche Import gleicht ausschließlich hierüber ab.
/// </param>
/// <param name="Capacity">Erwartete Sammelmenge in Kapazitätseinheiten.</param>
/// <param name="Latitude">Breitengrad.</param>
/// <param name="Longitude">Längengrad.</param>
/// <param name="Priority">
/// Priorität 0–100. Werte bis <c>MinimumPriorityFactor * 100</c> (Vorgabe 25) sind wirkungsgleich.
/// </param>
/// <param name="Skill">Die erforderliche Fraktion.</param>
/// <param name="ServiceTimeSeconds">
/// Bedienzeit in Sekunden; <c>null</c> bedeutet, dass die globale Vorgabe gilt.
/// </param>
/// <param name="Data">
/// Beliebige clientseitige Rohdaten (üblicherweise JSON), die der Server nicht interpretiert.
/// <c>null</c>, solange kein Client sie über <c>POST /api/Jobs/{id}/data</c> gesetzt hat. Siehe
/// <see cref="Models.Jobs.Job.Data"/>.
/// </param>
/// <remarks>
/// <b>Entfallene Felder:</b> <c>allowManualAssignment</c> (wirkungslos, mehrdeutig, beim Anlegen
/// nie gespeichert) und <c>deliveryDestination</c> (fester Ablieferungsort je Sammelpunkt, ohne
/// Wirkung auf die Planung). Abladestellen liegen unter <c>/api/DeliveryDestinations</c>.
/// </remarks>
public sealed record JobResponse(
    int Id, string Name, string? Description, string? ExternalReference,
    double Capacity, double Latitude, double Longitude, int Priority,
    SkillResponse? Skill, int? ServiceTimeSeconds, string? Data);

/// <summary>
/// Die clientseitigen Rohdaten eines einzelnen Sammelpunkts, isoliert vom übrigen Sammelpunkt.
/// </summary>
/// <param name="JobId">Der Sammelpunkt, zu dem die Rohdaten gehören.</param>
/// <param name="Data">
/// Die Rohdaten selbst (üblicherweise JSON, aber nicht als solches validiert); <c>null</c>, wenn
/// noch keine gesetzt wurden.
/// </param>
/// <remarks>
/// Eigener, schlanker Antworttyp für <c>GET /api/Jobs/{id}/data</c> und
/// <c>POST /api/Jobs/{id}/data</c> – ein Aufrufer, der ausschließlich dieses Feld lesen oder
/// schreiben will, muss dafür weder den vollständigen Sammelpunkt (inkl. Fraktion) laden noch
/// dessen übrige Pflichtfelder im Body mitschicken.
/// </remarks>
public sealed record JobDataResponse(int JobId, string? Data);

/// <summary>
/// Ein einzelner Halt einer berechneten Route.
/// </summary>
/// <param name="Sequence">Position in der Fahrtreihenfolge, beginnend bei 0.</param>
/// <param name="Type">
/// Art des Halts: <c>Start</c>, <c>Collection</c>, <c>Disposal</c> oder <c>End</c>.
/// </param>
/// <param name="JobId">Der bediente Sammelpunkt; nur bei <c>Collection</c> gesetzt.</param>
/// <param name="DeliveryDestinationId">Die genutzte Abladestelle; nur bei <c>Disposal</c> gesetzt.</param>
/// <param name="Arrival">Geplanter Ankunftszeitpunkt.</param>
/// <param name="TravelTimeFromStart">
/// Aufsummierte reine <b>Fahrzeit</b> vom Schichtstart bis zu diesem Halt, ohne Bedienzeiten.
/// </param>
/// <param name="ServiceTime">Bedienzeit an diesem Halt.</param>
/// <param name="DistanceFromStartMeters">Aufsummierte Fahrstrecke vom Schichtstart in Metern.</param>
/// <param name="LoadUnitsAfter">
/// Ladung <em>nach</em> diesem Halt in echten Kapazitätseinheiten (z. B. <c>2.4</c>). An einer
/// Abladestelle stets <c>0</c>.
/// </param>
/// <param name="Description">Anzeigename des Halts.</param>
/// <param name="Latitude">Breitengrad.</param>
/// <param name="Longitude">Längengrad.</param>
/// <remarks>
/// <para>
/// <b>Umbenannt gegenüber der Vorversion:</b> <c>duration</c> → <c>travelTimeFromStart</c>
/// (gespeichert war nie die Dauer dieses Halts, sondern die kumulierte Fahrzeit),
/// <c>service</c> → <c>serviceTime</c>, <c>distance</c> → <c>distanceFromStartMeters</c>
/// (Einheit und Bezugspunkt jetzt im Namen), <c>load</c> → <c>loadUnitsAfter</c>.
/// </para>
/// <para>
/// <b>Wichtig:</b> <c>loadUnitsAfter</c> ist eine <b>Zahl</b>, nicht mehr ein Array. Zuvor war es
/// ein JSON-Array mit stets genau einem Element, das aus einer Zeichenketten-Spalte stammte. Der
/// Wert ist nicht skaliert – es darf weder durch 1000 geteilt noch anders umgerechnet werden.
/// </para>
/// <para>
/// <b>Entfallen:</b> <c>violations</c> (in jeder Antwort leer, kein Schreibpfad) und
/// <c>waitingTime</c> (immer null).
/// </para>
/// </remarks>
public sealed record StepResponse(
    int Sequence, StepType Type, int? JobId, int? DeliveryDestinationId,
    DateTimeOffset Arrival, TimeSpan TravelTimeFromStart, TimeSpan ServiceTime,
    int DistanceFromStartMeters, double LoadUnitsAfter,
    string? Description, double Latitude, double Longitude);

/// <summary>
/// Die Emissionsbilanz einer Route.
/// </summary>
/// <param name="Co2eGrams">Berechnete CO2e-Emissionen in Gramm.</param>
/// <param name="CollectedUnits">Eingesammelte Menge in Kapazitätseinheiten.</param>
/// <param name="GramsPerUnit">
/// Emissionen je eingesammelte Kapazitätseinheit – die eigentliche Effizienzkennzahl. Nur so ist
/// ein Vergleich zweier Touren mit unterschiedlicher Sammelmenge zulässig.
/// </param>
/// <param name="FactorSource">Quelle der verwendeten Emissionsfaktoren.</param>
public sealed record EmissionsResponse(
    double? Co2eGrams,
    double? CollectedUnits,
    double? GramsPerUnit,
    string? FactorSource);

/// <summary>
/// Eine vollständige Route inklusive Sammelpunkten, Fraktionen, Fahrzeug und Halten.
/// </summary>
/// <param name="Id">Kennung.</param>
/// <param name="Name">Anzeigename.</param>
/// <param name="Description">Freitext.</param>
/// <param name="ExternalReference">Kennung im Fremdsystem.</param>
/// <param name="Color">RGBA-Farbe (0xRRGGBBAA).</param>
/// <param name="StartDate">Beginn des Schichtfensters.</param>
/// <param name="EndDate">Ende des Schichtfensters.</param>
/// <param name="StartLatitude">Breitengrad des Startpunkts.</param>
/// <param name="StartLongitude">Längengrad des Startpunkts.</param>
/// <param name="EndLatitude">Breitengrad des Zielpunkts.</param>
/// <param name="EndLongitude">Längengrad des Zielpunkts.</param>
/// <param name="Geometry">Die gefahrene Strecke als Encoded Polyline, Präzision 1e-6.</param>
/// <param name="Meters">Gefahrene Strecke in Metern.</param>
/// <param name="Service">Summe aller Bedienzeiten.</param>
/// <param name="Duration">Reine Fahrzeit, ohne Bedienzeiten.</param>
/// <param name="FillShift">Ob die Schicht bewusst ausgefüllt wurde.</param>
/// <param name="Emissions">Die CO2e-Bilanz.</param>
/// <param name="JobOrder">
/// Die bedienten Sammelpunkte in Fahrtreihenfolge. <b>Abgeleitet</b> aus <paramref name="Steps"/>
/// und nicht mehr separat gespeichert.
/// </param>
/// <param name="Jobs">Die bedienten Sammelpunkte (ungeordnet; Reihenfolge siehe JobOrder/Steps).</param>
/// <param name="Skills">Die Fraktionen der Route.</param>
/// <param name="Vehicle">Das zugewiesene Fahrzeug.</param>
/// <param name="Steps">Die Halte in Fahrtreihenfolge, inklusive Start, Entleerungen und Ende.</param>
/// <remarks>
/// <b>Entfallen:</b> <c>waitingTime</c> (immer null).
/// </remarks>
public sealed record RouteResponse(
    int Id, string Name, string? Description, string? ExternalReference,
    uint Color, DateTimeOffset StartDate, DateTimeOffset EndDate,
    double StartLatitude, double StartLongitude, double EndLatitude, double EndLongitude,
    string? Geometry, int? Meters, TimeSpan? Service, TimeSpan? Duration,
    bool FillShift, EmissionsResponse Emissions,
    IReadOnlyList<int> JobOrder, IReadOnlyList<JobResponse> Jobs,
    IReadOnlyList<SkillResponse> Skills, VehicleResponse? Vehicle, IReadOnlyList<StepResponse> Steps);

/// <summary>Kompakte Routen-Darstellung für Listen-Endpunkte (ohne Sammelpunkte und Halte).</summary>
public sealed record RouteSummaryResponse(
    int Id, string Name, string? Description, uint Color,
    DateTimeOffset StartDate, DateTimeOffset EndDate,
    int JobCount, int StepCount, int? Meters, TimeSpan? Duration,
    bool FillShift, double? Co2eGrams, int? VehicleId);

/// <summary>Eine wiederholende (Frequenz-)Route.</summary>
public sealed record FrequencyRouteResponse(
    int Id, string Name, string? Description, string? ExternalReference,
    DateTimeOffset StartDate, DateTimeOffset EndDate, TimeSpan Frequency,
    uint Color, double StartLatitude, double StartLongitude, double EndLatitude, double EndLongitude,
    bool FillShift,
    IReadOnlyList<SkillResponse> Skills, VehicleResponse? Vehicle);

/// <summary>
/// Eine Abladestelle.
/// </summary>
/// <param name="Id">Kennung.</param>
/// <param name="Name">Anzeigename.</param>
/// <param name="Description">Freitext.</param>
/// <param name="ExternalReference">Kennung im Fremdsystem.</param>
/// <param name="Latitude">Breitengrad.</param>
/// <param name="Longitude">Längengrad.</param>
/// <param name="MaximumCapacity">
/// Aufnahmefähigkeit in Kapazitätseinheiten. Werte kleiner oder gleich 0 bedeuten unbegrenzt.
/// </param>
/// <param name="Skills">
/// Die angenommenen Fraktionen. <b>Leere Liste bedeutet: nimmt alle Fraktionen an.</b>
/// </param>
/// <param name="ServiceTimeSeconds">
/// Feste Bedienzeit einer Entleerung in Sekunden; <c>null</c> bedeutet, dass die globale Vorgabe gilt.
/// </param>
/// <param name="ServiceSecondsPerUnit">Zusätzliche Bedienzeit je abgeladener Kapazitätseinheit.</param>
public sealed record DeliveryDestinationResponse(
    int Id, string Name, string? Description, string? ExternalReference,
    double Latitude, double Longitude, double MaximumCapacity,
    IReadOnlyList<SkillResponse> Skills,
    int? ServiceTimeSeconds, double? ServiceSecondsPerUnit);

/// <summary>Ein Benutzerkonto – ohne <c>PasswordHash</c>, strukturell ausgeschlossen.</summary>
public sealed record UserAccountResponse(
    int Id, string Username, string Email, string FirstName, string LastName,
    bool IsActive, string ProfilePictureUrl, DateTimeOffset CreatedAt, DateTimeOffset? LastLogin,
    string ApplicationRights, string AdministrativeRights);

/// <summary>Die aufgelösten Rechte eines Benutzers.</summary>
public sealed record UserPermissionsResponse(string AdministrativeRights, string ApplicationRights);

/// <summary>Ergebnis einer Bulk-Erstellung von Benutzern.</summary>
public sealed record BulkCreateResultResponse(
    IReadOnlyList<UserAccountResponse> CreatedAccounts,
    IReadOnlyList<string> Errors);

/// <summary>Ergebnis einer Bulk-Operation (Aktivieren/Deaktivieren/Löschen).</summary>
public sealed record BulkOperationResultResponse(int SuccessCount, IReadOnlyList<string> Errors);

/// <summary>Benutzer-Statistiken.</summary>
public sealed record UserStatisticsResponse(
    int TotalUsers, int ActiveUsers, int InactiveUsers, int RecentlyCreatedUsers,
    IReadOnlyDictionary<string, int> UsersByAdministrativeRights,
    IReadOnlyDictionary<string, int> UsersByApplicationRights);
