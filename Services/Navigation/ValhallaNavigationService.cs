using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SmartRoutingAPI.Models.Misc;
using SmartRoutingAPI.Models.Navigation;
using SmartRoutingAPI.Services.Routing;

namespace SmartRoutingAPI.Services.Navigation;

/// <summary>
/// Eine Navigationsanfrage in ihrer internen, bereits geprüften Form.
/// </summary>
/// <param name="Origin">Die Startposition, üblicherweise die gemeldete Fahrzeugposition.</param>
/// <param name="Waypoints">Zwischenziele in Fahrtreihenfolge.</param>
/// <param name="Destination">Das Ziel.</param>
/// <param name="ExcludeLocations">Zu meidende Punkte (gesperrte Straßen).</param>
/// <param name="ExcludePolygons">Zu meidende Flächen.</param>
/// <param name="Language">Sprache der Anweisungstexte; <c>null</c> nutzt die Konfiguration.</param>
/// <param name="DecodeGeometry">Ob die Geometrie serverseitig zusätzlich dekodiert wird.</param>
public sealed record GuidanceQuery(
    NavigationPosition Origin,
    IReadOnlyList<GeoPoint> Waypoints,
    GeoPoint Destination,
    IReadOnlyList<GeoPoint> ExcludeLocations,
    IReadOnlyList<ExclusionZone> ExcludePolygons,
    string? Language,
    bool DecodeGeometry);

/// <summary>
/// Erzeugt Abbiegehinweise („turn-by-turn“) für den Fahrer.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Berechnet die Fahrhinweise von der gemeldeten Position zum Ziel.
    /// </summary>
    /// <param name="query">Die geprüfte Anfrage.</param>
    /// <param name="cancellationToken">Token zum Abbrechen (Client-Trennung).</param>
    /// <returns>Die Hinweise samt Geometrie und Kennzahlen.</returns>
    /// <exception cref="NavigationException">Die Anfrage ist fachlich nicht zulässig.</exception>
    /// <exception cref="ValhallaException">Die Routing-Instanz konnte nicht antworten.</exception>
    Task<DirectionsResult> GetDirectionsAsync(GuidanceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="INavigationService"/> auf Basis der selbst gehosteten Valhalla-Instanz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Der entscheidende Unterschied zur Routenplanung.</b> Die Planung ruft <c>/route</c> mit
/// <c>directions_type = "none"</c> auf: Sie braucht nur Geometrie und Kennzahlen, und
/// Abbiegehinweise hätten die Antwort um ein Vielfaches vergrößert. Für einen Fahrer ist genau
/// dieser weggelassene Teil der eigentliche Inhalt. Dieser Dienst fordert daher
/// <c>directions_type = "instructions"</c> samt Sprache an.
/// </para>
/// <para>
/// <b>Dasselbe Kostenmodell wie die Planung.</b> Über <see cref="ValhallaCostingProfile"/>. Ein
/// eigenes Profil für die Navigation würde den Fahrer über Straßen führen, die der Planer nie
/// bewertet hat — die Tour wäre weder emissionsseitig noch in Kilometern das, was geplant wurde,
/// und die Abweichungserkennung würde dauerhaft Fehlalarm auslösen, weil die geplante Linie und die
/// angewiesene Strecke systematisch verschieden wären.
/// </para>
/// </remarks>
public sealed class ValhallaNavigationService : INavigationService
{
    private readonly HttpClient _http;
    private readonly ValhallaCostingProfile _costing;
    private readonly NavigationOptions _options;
    private readonly ILogger<ValhallaNavigationService> _logger;

    /// <summary>Web-Vorgaben; <c>null</c>-Felder werden beim Senden weggelassen.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Valhalla liefert Längen in der angeforderten Einheit; hier stets Kilometer.</summary>
    private const double MetersPerKilometer = 1000.0;

    /// <summary>Erzeugt den Dienst.</summary>
    /// <param name="http">Typisierter HTTP-Client mit Basisadresse und Timeout aus der Konfiguration.</param>
    /// <param name="costing">Das gemeinsame Kostenmodell von Planung und Navigation.</param>
    /// <param name="options">Die beim Start geprüfte Konfiguration.</param>
    /// <param name="logger">Logger für die serverseitige Diagnose.</param>
    /// <exception cref="ArgumentNullException">Ein Argument ist <c>null</c>.</exception>
    public ValhallaNavigationService(
        HttpClient http,
        ValhallaCostingProfile costing,
        IOptions<RoutingOptions> options,
        ILogger<ValhallaNavigationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = http ?? throw new ArgumentNullException(nameof(http));
        _costing = costing ?? throw new ArgumentNullException(nameof(costing));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value.Navigation;

        if (_costing.SovereignRightsEnabled && _options.ApplySovereignRights)
        {
            // Fahrhinweise unter Sonderrechten sind nachweispflichtig: Sie weisen einen Menschen an,
            // von den Verkehrsregeln abzuweichen.
            _logger.LogInformation(
                "Fahrhinweise werden unter Sonderrechten erteilt. Rechtsgrundlage: {LegalBasis}.",
                _costing.SovereignLegalBasis);
        }
    }

    /// <inheritdoc />
    public async Task<DirectionsResult> GetDirectionsAsync(
        GuidanceQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = BuildRequest(query);

            var response = await ValhallaHttpExchange
                .PostAsync<NavRouteResponse>(_http, "/route", request, SerializerOptions, _logger, cancellationToken)
                .ConfigureAwait(false);

            return MapResult(response, query.DecodeGeometry);
        }
        catch (NavigationException)
        {
            throw;
        }
        catch (ValhallaException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei der Berechnung der Fahrhinweise.");
            throw new NavigationException(
                "Die Fahrhinweise konnten nicht berechnet werden.",
                StatusCodes.Status500InternalServerError,
                ex);
        }
    }

    // =============================================================================================
    // Anfrageaufbau
    // =============================================================================================

    /// <summary>
    /// Prüft die Anfrage gegen die konfigurierten Grenzen und baut die Valhalla-Anfrage auf.
    /// </summary>
    /// <exception cref="NavigationException">Eine Grenze ist überschritten oder eine Koordinate ungültig.</exception>
    private NavRouteRequest BuildRequest(GuidanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Waypoints.Count > _options.MaxIntermediateWaypoints)
        {
            throw new NavigationException(
                $"Es sind höchstens {_options.MaxIntermediateWaypoints} Zwischenziele je Anfrage zulässig.");
        }

        if (query.ExcludeLocations.Count > _options.MaxExcludeLocations)
        {
            throw new NavigationException(
                $"Es sind höchstens {_options.MaxExcludeLocations} Sperrpunkte je Anfrage zulässig.");
        }

        if (query.ExcludePolygons.Count > _options.MaxExcludePolygons)
        {
            throw new NavigationException(
                $"Es sind höchstens {_options.MaxExcludePolygons} Sperrflächen je Anfrage zulässig.");
        }

        var locations = new List<NavLocation>(query.Waypoints.Count + 2)
        {
            BuildOrigin(query.Origin)
        };

        foreach (var waypoint in query.Waypoints)
        {
            locations.Add(BuildLocation(waypoint, "through"));
        }

        locations.Add(BuildLocation(query.Destination, "break"));

        bool applySovereign = _options.ApplySovereignRights && _costing.SovereignRightsEnabled;

        return new NavRouteRequest
        {
            Locations = locations,
            Costing = _costing.Costing,
            CostingOptions = _costing.ForGuidance(_options.ApplySovereignRights),
            Units = "kilometers",

            // Der eigentliche Zweck dieses Dienstes.
            DirectionsType = "instructions",

            Language = string.IsNullOrWhiteSpace(query.Language)
                ? _options.Language
                : query.Language.Trim(),

            ExcludeLocations = query.ExcludeLocations.Count == 0
                ? null
                : query.ExcludeLocations.Select(p => BuildLocation(p, type: null)).ToList(),

            ExcludePolygons = BuildExcludePolygons(query.ExcludePolygons),

            // Sonderrechte sind in der Antwort auszuweisen, damit die Fahreroberfläche sie
            // kennzeichnen kann. Der Wert wird hier nicht gesendet, sondern nur festgehalten.
            SovereignRightsApplied = applySovereign
        };
    }

    /// <summary>
    /// Baut die Startposition auf — inklusive Fahrtrichtung, sofern gemeldet.
    /// </summary>
    /// <remarks>
    /// <b>Die Fahrtrichtung ist der Unterschied zwischen einer brauchbaren und einer unbrauchbaren
    /// Neuberechnung.</b> Ohne sie ordnet Valhalla das Fahrzeug ebenso wahrscheinlich der
    /// Gegenrichtung derselben Straße zu; die erste Anweisung wäre dann eine Wende, die der Fahrer
    /// nicht nachvollziehen kann und die er zu Recht ignoriert.
    /// </remarks>
    private NavLocation BuildOrigin(NavigationPosition origin)
    {
        var location = BuildLocation(
            new GeoPoint { Latitude = origin.Latitude, Longitude = origin.Longitude },
            "break");

        if (origin.Heading is { } heading)
        {
            // Valhalla erwartet 0…360; 360 ist gleichbedeutend mit 0.
            location.Heading = (int)Math.Round(heading % 360.0, MidpointRounding.AwayFromZero);
            location.HeadingTolerance = _options.HeadingToleranceDegrees;
        }

        return location;
    }

    /// <summary>Prüft eine Koordinate und baut daraus eine Valhalla-Position.</summary>
    /// <exception cref="NavigationException">Die Koordinate liegt außerhalb des gültigen Bereichs.</exception>
    private static NavLocation BuildLocation(GeoPoint point, string? type)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (point.Latitude is < -90.0 or > 90.0 || point.Longitude is < -180.0 or > 180.0)
        {
            throw new NavigationException(
                "Mindestens eine Koordinate liegt außerhalb des gültigen Bereichs.");
        }

        return new NavLocation
        {
            Lat = point.Latitude,
            Lon = point.Longitude,
            Type = type
        };
    }

    /// <summary>
    /// Übersetzt die Sperrflächen in Valhallas verschachtelte Ringdarstellung
    /// (<c>[[[lon, lat], …]]</c>).
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge ist <b>Längengrad zuerst</b> — GeoJSON-Konvention und damit umgekehrt zu
    /// allen anderen Koordinatenangaben dieser API. Eine Verwechslung ergibt keine Fehlermeldung,
    /// sondern eine Sperrfläche im Golf von Guinea, die wirkungslos bleibt.
    /// </remarks>
    /// <exception cref="NavigationException">Ein Ring ist zu klein, zu groß oder ungültig.</exception>
    private List<List<double[]>>? BuildExcludePolygons(IReadOnlyList<ExclusionZone> zones)
    {
        if (zones.Count == 0)
        {
            return null;
        }

        var polygons = new List<List<double[]>>(zones.Count);

        foreach (var zone in zones)
        {
            if (zone?.Ring is null || zone.Ring.Count < 3)
            {
                throw new NavigationException("Eine Sperrfläche benötigt mindestens 3 Eckpunkte.");
            }

            if (zone.Ring.Count > _options.MaxExcludePolygonVertices)
            {
                throw new NavigationException(
                    $"Eine Sperrfläche darf höchstens {_options.MaxExcludePolygonVertices} Eckpunkte haben.");
            }

            var ring = new List<double[]>(zone.Ring.Count);

            foreach (var vertex in zone.Ring)
            {
                if (vertex.Latitude is < -90.0 or > 90.0 || vertex.Longitude is < -180.0 or > 180.0)
                {
                    throw new NavigationException(
                        "Mindestens ein Eckpunkt einer Sperrfläche liegt außerhalb des gültigen Bereichs.");
                }

                ring.Add(new[] { vertex.Longitude, vertex.Latitude });
            }

            polygons.Add(ring);
        }

        return polygons;
    }

    // =============================================================================================
    // Antwortabbildung
    // =============================================================================================

    /// <summary>Übersetzt die Valhalla-Antwort in das öffentliche Ergebnismodell.</summary>
    /// <exception cref="NavigationException">Valhalla lieferte keine verwertbare Route.</exception>
    private DirectionsResult MapResult(NavRouteResponse response, bool decodeGeometry)
    {
        var trip = response.Trip
            ?? throw new NavigationException(
                "Die Routing-Instanz lieferte keine Route zurück.",
                StatusCodes.Status502BadGateway);

        var result = new DirectionsResult
        {
            LengthMeters = ToMeters(trip.Summary?.Length),
            TimeSeconds = trip.Summary?.Time ?? 0.0,
            Language = trip.Language,
            Costing = _costing.Costing,
            SovereignRightsApplied = _options.ApplySovereignRights && _costing.SovereignRightsEnabled
        };

        if (trip.Legs is null)
        {
            return result;
        }

        foreach (var leg in trip.Legs)
        {
            var mapped = new NavigationLeg
            {
                LengthMeters = ToMeters(leg.Summary?.Length),
                TimeSeconds = leg.Summary?.Time ?? 0.0,
                Shape = leg.Shape
            };

            if (decodeGeometry && !string.IsNullOrEmpty(leg.Shape))
            {
                mapped.Geometry = PolylineCodec.Decode(leg.Shape)
                    .Select(c => new GeoPoint { Latitude = c.Latitude, Longitude = c.Longitude })
                    .ToList();
            }

            if (leg.Maneuvers is not null)
            {
                foreach (var maneuver in leg.Maneuvers)
                {
                    mapped.Maneuvers.Add(MapManeuver(maneuver));
                }
            }

            result.Legs.Add(mapped);
        }

        return result;
    }

    /// <summary>Übersetzt ein einzelnes Manöver.</summary>
    private static NavigationManeuver MapManeuver(NavManeuver maneuver)
        => new()
        {
            Type = ToManeuverType(maneuver.Type),
            RawType = maneuver.Type,
            Instruction = maneuver.Instruction,
            VerbalTransitionAlertInstruction = maneuver.VerbalTransitionAlertInstruction,
            VerbalSuccinctTransitionInstruction = maneuver.VerbalSuccinctTransitionInstruction,
            VerbalPreTransitionInstruction = maneuver.VerbalPreTransitionInstruction,
            VerbalPostTransitionInstruction = maneuver.VerbalPostTransitionInstruction,
            StreetNames = maneuver.StreetNames,
            BeginStreetNames = maneuver.BeginStreetNames,
            LengthMeters = ToMeters(maneuver.Length),
            TimeSeconds = maneuver.Time ?? 0.0,
            BeginShapeIndex = maneuver.BeginShapeIndex ?? 0,
            EndShapeIndex = maneuver.EndShapeIndex ?? 0,
            BearingBefore = maneuver.BearingBefore,
            BearingAfter = maneuver.BearingAfter,
            RoundaboutExitCount = maneuver.RoundaboutExitCount,
            Toll = maneuver.Toll ?? false,
            Highway = maneuver.Highway ?? false,
            Ferry = maneuver.Ferry ?? false,
            Rough = maneuver.Rough ?? false,
            HasTimeRestrictions = maneuver.HasTimeRestrictions ?? false,
            TravelMode = maneuver.TravelMode,
            TravelType = maneuver.TravelType,
            Sign = MapSign(maneuver.Sign)
        };

    /// <summary>Übersetzt die Beschilderung eines Manövers; <c>null</c> bleibt <c>null</c>.</summary>
    private static ManeuverSign? MapSign(NavSign? sign)
    {
        if (sign is null)
        {
            return null;
        }

        var mapped = new ManeuverSign
        {
            ExitNumbers = MapSignElements(sign.ExitNumberElements),
            ExitBranches = MapSignElements(sign.ExitBranchElements),
            ExitToward = MapSignElements(sign.ExitTowardElements),
            ExitNames = MapSignElements(sign.ExitNameElements)
        };

        bool isEmpty = mapped.ExitNumbers is null
            && mapped.ExitBranches is null
            && mapped.ExitToward is null
            && mapped.ExitNames is null;

        return isEmpty ? null : mapped;
    }

    /// <summary>Übersetzt eine Liste von Beschilderungseinträgen.</summary>
    private static List<SignElement>? MapSignElements(List<NavSignElement>? elements)
    {
        if (elements is null || elements.Count == 0)
        {
            return null;
        }

        var mapped = new List<SignElement>(elements.Count);

        foreach (var element in elements)
        {
            if (string.IsNullOrWhiteSpace(element.Text))
            {
                continue;
            }

            mapped.Add(new SignElement(element.Text, element.ConsecutiveCount));
        }

        return mapped.Count == 0 ? null : mapped;
    }

    /// <summary>
    /// Übersetzt Valhallas ganzzahlige Manöverart in die benannte Aufzählung.
    /// </summary>
    /// <remarks>
    /// Ein unbekannter Wert ergibt <see cref="ManeuverType.Unknown"/> — nicht
    /// <see cref="ManeuverType.None"/>. „Unbekannt“ und „kein Manöver“ auf denselben Wert
    /// abzubilden hieße, einer neuen Valhalla-Version stillschweigend eine falsche Bedeutung
    /// zuzuschreiben.
    /// </remarks>
    private static ManeuverType ToManeuverType(int rawType)
        => Enum.IsDefined(typeof(ManeuverType), rawType) && rawType >= 0
            ? (ManeuverType)rawType
            : ManeuverType.Unknown;

    /// <summary>Rechnet eine in Kilometern angeforderte Länge in Meter um.</summary>
    private static double ToMeters(double? kilometers)
    {
        if (kilometers is null || double.IsNaN(kilometers.Value) || double.IsInfinity(kilometers.Value))
        {
            return 0.0;
        }

        return Math.Max(0.0, kilometers.Value * MetersPerKilometer);
    }
}

// =================================================================================================
// Valhalla-Wire-Modelle. Bewusst 'internal': sie sind kein Teil des öffentlichen API-Vertrags und
// dürfen sich mit der Valhalla-Version ändern, ohne den Vertrag dieser API zu berühren.
//
// Alle Zahlenfelder sind nullbar und werden über die duldsamen Leser gebunden. Grund: Valhalla
// lässt Felder mit Standardwert weg, schreibt bei Wiederberechnungen ausdrücklich 'null' und
// ersetzt Zahlen bei Sonderfällen durch Wortmarken. Eine strenge Bindung würde eine vollständig
// gültige Antwort an einem einzigen Sonderfall scheitern lassen — genau der Fehler, der
// /api/MapMatching/match zum Ausfall gebracht hat.
// =================================================================================================

internal sealed class NavRouteRequest
{
    [JsonPropertyName("locations")]
    public List<NavLocation> Locations { get; set; } = new();

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "truck";

    [JsonPropertyName("costing_options")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? CostingOptions { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = "kilometers";

    [JsonPropertyName("directions_type")]
    public string DirectionsType { get; set; } = "instructions";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("exclude_locations")]
    public List<NavLocation>? ExcludeLocations { get; set; }

    [JsonPropertyName("exclude_polygons")]
    public List<List<double[]>>? ExcludePolygons { get; set; }

    /// <summary>
    /// Nur zur Weitergabe innerhalb dieses Prozesses; wird nicht an Valhalla gesendet.
    /// </summary>
    [JsonIgnore]
    public bool SovereignRightsApplied { get; set; }
}

internal sealed class NavLocation
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("heading")]
    public int? Heading { get; set; }

    [JsonPropertyName("heading_tolerance")]
    public int? HeadingTolerance { get; set; }
}

internal sealed class NavRouteResponse
{
    [JsonPropertyName("trip")]
    public NavTrip? Trip { get; set; }
}

internal sealed class NavTrip
{
    [JsonPropertyName("legs")]
    public List<NavLeg>? Legs { get; set; }

    [JsonPropertyName("summary")]
    public NavSummary? Summary { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? Status { get; set; }

    [JsonPropertyName("status_message")]
    public string? StatusMessage { get; set; }
}

internal sealed class NavLeg
{
    [JsonPropertyName("maneuvers")]
    public List<NavManeuver>? Maneuvers { get; set; }

    [JsonPropertyName("summary")]
    public NavSummary? Summary { get; set; }

    [JsonPropertyName("shape")]
    public string? Shape { get; set; }
}

internal sealed class NavSummary
{
    [JsonPropertyName("length")]
    [JsonConverter(typeof(Json.TolerantNullableDoubleConverter))]
    public double? Length { get; set; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(Json.TolerantNullableDoubleConverter))]
    public double? Time { get; set; }
}

internal sealed class NavManeuver
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    [JsonPropertyName("verbal_transition_alert_instruction")]
    public string? VerbalTransitionAlertInstruction { get; set; }

    [JsonPropertyName("verbal_succinct_transition_instruction")]
    public string? VerbalSuccinctTransitionInstruction { get; set; }

    [JsonPropertyName("verbal_pre_transition_instruction")]
    public string? VerbalPreTransitionInstruction { get; set; }

    [JsonPropertyName("verbal_post_transition_instruction")]
    public string? VerbalPostTransitionInstruction { get; set; }

    [JsonPropertyName("street_names")]
    public List<string>? StreetNames { get; set; }

    [JsonPropertyName("begin_street_names")]
    public List<string>? BeginStreetNames { get; set; }

    [JsonPropertyName("length")]
    [JsonConverter(typeof(Json.TolerantNullableDoubleConverter))]
    public double? Length { get; set; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(Json.TolerantNullableDoubleConverter))]
    public double? Time { get; set; }

    [JsonPropertyName("begin_shape_index")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? BeginShapeIndex { get; set; }

    [JsonPropertyName("end_shape_index")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? EndShapeIndex { get; set; }

    [JsonPropertyName("bearing_before")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? BearingBefore { get; set; }

    [JsonPropertyName("bearing_after")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? BearingAfter { get; set; }

    [JsonPropertyName("roundabout_exit_count")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? RoundaboutExitCount { get; set; }

    [JsonPropertyName("toll")]
    public bool? Toll { get; set; }

    [JsonPropertyName("highway")]
    public bool? Highway { get; set; }

    [JsonPropertyName("ferry")]
    public bool? Ferry { get; set; }

    [JsonPropertyName("rough")]
    public bool? Rough { get; set; }

    [JsonPropertyName("has_time_restrictions")]
    public bool? HasTimeRestrictions { get; set; }

    [JsonPropertyName("travel_mode")]
    public string? TravelMode { get; set; }

    [JsonPropertyName("travel_type")]
    public string? TravelType { get; set; }

    [JsonPropertyName("sign")]
    public NavSign? Sign { get; set; }
}

internal sealed class NavSign
{
    [JsonPropertyName("exit_number_elements")]
    public List<NavSignElement>? ExitNumberElements { get; set; }

    [JsonPropertyName("exit_branch_elements")]
    public List<NavSignElement>? ExitBranchElements { get; set; }

    [JsonPropertyName("exit_toward_elements")]
    public List<NavSignElement>? ExitTowardElements { get; set; }

    [JsonPropertyName("exit_name_elements")]
    public List<NavSignElement>? ExitNameElements { get; set; }
}

internal sealed class NavSignElement
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("consecutive_count")]
    [JsonConverter(typeof(Json.TolerantNullableInt32Converter))]
    public int? ConsecutiveCount { get; set; }
}
