using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SmartRoutingAPI.Models.Misc;
using SmartRoutingAPI.Services.Json;
using SmartRoutingAPI.Services.Routing;

namespace SmartRoutingAPI.Services;

/// <summary>
/// Wird geworfen, wenn eine Anfrage an die Valhalla-Instanz fehlschlägt. Trägt einen passenden
/// HTTP-Statuscode, damit der aufrufende Controller dem Client eine korrekte Antwort geben kann,
/// ohne interne Details preiszugeben.
/// </summary>
public sealed class ValhallaException : Exception
{
    /// <summary>Der HTTP-Statuscode, mit dem der Controller antworten sollte.</summary>
    public int SuggestedStatusCode { get; }

    /// <summary>Erzeugt eine neue <see cref="ValhallaException"/>.</summary>
    /// <param name="message">Für den Client geeignete, nicht sensible Fehlermeldung.</param>
    /// <param name="suggestedStatusCode">Vorgeschlagener HTTP-Statuscode für die Antwort.</param>
    /// <param name="innerException">Optionale zugrunde liegende Ausnahme (bleibt serverseitig).</param>
    public ValhallaException(string message, int suggestedStatusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        SuggestedStatusCode = suggestedStatusCode;
    }
}

/// <summary>
/// Abstraktion über die Map-Matching-Funktionen (Meili) einer Valhalla-Instanz.
/// </summary>
public interface IValhallaService
{
    /// <summary>
    /// Gleicht eine GPS-Spur über <c>trace_attributes</c> auf das Straßennetz ab und liefert die
    /// gematchten Punkte, die gesnappte Geometrie und optional die Kantenattribute.
    /// </summary>
    /// <param name="request">Die Map-Matching-Anfrage.</param>
    /// <param name="cancellationToken">Token zum Abbrechen (z. B. bei Client-Trennung).</param>
    /// <returns>Das Map-Matching-Ergebnis.</returns>
    /// <exception cref="ValhallaException">Bei ungültiger Eingabe oder Problemen mit der Valhalla-Instanz.</exception>
    Task<MapMatchResult> TraceAttributesAsync(MapMatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gleicht eine GPS-Spur über <c>trace_route</c> auf das Straßennetz ab und liefert die gesnappte
    /// Route inklusive Länge, Dauer und Teilgeometrien.
    /// </summary>
    /// <param name="request">Die Map-Matching-Anfrage.</param>
    /// <param name="cancellationToken">Token zum Abbrechen (z. B. bei Client-Trennung).</param>
    /// <returns>Die gesnappte Route.</returns>
    /// <exception cref="ValhallaException">Bei ungültiger Eingabe oder Problemen mit der Valhalla-Instanz.</exception>
    Task<MapRouteResult> TraceRouteAsync(MapMatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prüft über den <c>/status</c>-Endpunkt, ob die Valhalla-Instanz erreichbar und einsatzbereit ist.
    /// </summary>
    /// <param name="cancellationToken">Token zum Abbrechen.</param>
    /// <returns><c>true</c>, wenn die Instanz antwortet; sonst <c>false</c>.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Standardimplementierung von <see cref="IValhallaService"/>. Nutzt einen typisierten, gepoolten
/// <see cref="HttpClient"/> (via <c>IHttpClientFactory</c>) und spricht direkt die Meili-Endpunkte
/// (<c>/trace_attributes</c>, <c>/trace_route</c>, <c>/status</c>) der Valhalla-Instanz an.
/// </summary>
/// <remarks>
/// <para>
/// <b>Abgrenzung zur Live-Navigation.</b> Dieser Dienst gleicht <em>aufgezeichnete</em> GPS-Spuren
/// gegen das gesamte Straßennetz ab (Hidden-Markov-Verfahren). Er beantwortet die Frage „welche
/// Straßen wurden befahren?“ und ist damit die Grundlage für die nachträgliche Bewertung einer Tour.
/// Für die Frage „ist dieses Fahrzeug noch auf Route X?“ ist er das falsche Werkzeug — dafür ist der
/// Lotfußpunkt auf die bereits berechnete Streckengeometrie exakt und ohne Netzaufruf zu bestimmen,
/// siehe <c>SmartRoutingAPI.Services.Navigation.PlannedPath</c>.
/// </para>
/// </remarks>
public sealed class ValhallaService : IValhallaService
{
    private readonly HttpClient _http;
    private readonly ILogger<ValhallaService> _logger;

    // Web-Defaults liefern camelCase-Deserialisierung; Nulls werden beim Senden weggelassen,
    // damit optionale Meili-Felder nicht als "null" übertragen werden.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // --- Sicherheits-Whitelists ---------------------------------------------------------------
    // Eingaben werden gegen bekannte Werte geprüft, damit niemals beliebige Strings an die
    // Routing-Engine durchgereicht werden (Defense-in-Depth, verhindert Parameter-Injection).
    private static readonly HashSet<string> AllowedCosting = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "auto_shorter", "truck", "bus", "taxi",
        "bicycle", "pedestrian", "motorcycle", "motor_scooter"
    };

    private static readonly HashSet<string> AllowedShapeMatch = new(StringComparer.OrdinalIgnoreCase)
    {
        "edge_walk", "map_snap", "walk_or_snap"
    };

    private static readonly HashSet<string> AllowedPointType = new(StringComparer.OrdinalIgnoreCase)
    {
        "break", "via", "through", "break_through"
    };

    // Meili-Performance-Leitlinie: ~1 Punkt/s bis 1 Punkt/10s. Sehr lange Traces werden abgewiesen,
    // um Speicher-/CPU-Spikes und potenzielle DoS-Vektoren zu vermeiden.
    private const int MaxTracePoints = 5000;

    // Valhalla begrenzt den Suchradius hart auf 100 m.
    private const int MaxSearchRadius = 100;

    /// <summary>Erzeugt den Dienst mit dem typisierten <see cref="HttpClient"/> und einem Logger.</summary>
    /// <param name="http">Der von <c>AddHttpClient</c> bereitgestellte, vorkonfigurierte Client.</param>
    /// <param name="logger">Logger für serverseitige Diagnose.</param>
    public ValhallaService(HttpClient http, ILogger<ValhallaService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MapMatchResult> TraceAttributesAsync(MapMatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // Eingabevalidierung + Aufbau der Meili-Anfrage (wirft bei ungültiger Eingabe 400).
            var meiliRequest = BuildMeiliRequest(request);
            meiliRequest.Filters = BuildAttributeFilter(request.IncludeEdges);

            var response = await ValhallaHttpExchange
                .PostAsync<MeiliTraceAttributesResponse>(
                    _http, "/trace_attributes", meiliRequest, SerializerOptions, _logger, cancellationToken)
                .ConfigureAwait(false);

            return MapTraceAttributes(request, response);
        }
        catch (ValhallaException)
        {
            // Bereits klassifizierter Fehler: unverändert weiterreichen (Stacktrace bleibt erhalten).
            throw;
        }
        catch (OperationCanceledException)
        {
            // Ein Abbruch durch den Aufrufer ist kein Fehler und darf nicht als 500 erscheinen.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler beim Map-Matching (trace_attributes).");
            throw new ValhallaException("Unerwarteter Fehler beim Map-Matching.", StatusCodes.Status500InternalServerError, ex);
        }
    }

    /// <inheritdoc />
    public async Task<MapRouteResult> TraceRouteAsync(MapMatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var meiliRequest = BuildMeiliRequest(request);

            var response = await ValhallaHttpExchange
                .PostAsync<MeiliTraceRouteResponse>(
                    _http, "/trace_route", meiliRequest, SerializerOptions, _logger, cancellationToken)
                .ConfigureAwait(false);

            return MapTraceRoute(request, response);
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
            _logger.LogError(ex, "Unerwarteter Fehler beim Map-Matching (trace_route).");
            throw new ValhallaException("Unerwarteter Fehler beim Map-Matching.", StatusCodes.Status500InternalServerError, ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/status", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Erreichbarkeitsprüfung darf niemals werfen – sie liefert bewusst nur true/false.
            _logger.LogWarning(ex, "Valhalla-Statusprüfung fehlgeschlagen.");
            return false;
        }
    }

    // ==========================================================================================
    // Interne Hilfsmethoden
    // ==========================================================================================

    /// <summary>
    /// Validiert die eingehende Anfrage und baut daraus die Meili-Wire-Struktur auf.
    /// </summary>
    /// <exception cref="ValhallaException">Statuscode 400 bei ungültiger Eingabe.</exception>
    private static MeiliRequest BuildMeiliRequest(MapMatchRequest request)
    {
        try
        {
            if (request is null || request.Points is null || request.Points.Count < 2)
            {
                throw new ValhallaException("Es werden mindestens 2 GPS-Punkte benötigt.", StatusCodes.Status400BadRequest);
            }

            if (request.Points.Count > MaxTracePoints)
            {
                throw new ValhallaException(
                    $"Die Spur überschreitet das Maximum von {MaxTracePoints} Punkten.",
                    StatusCodes.Status400BadRequest);
            }

            var costing = string.IsNullOrWhiteSpace(request.Costing) ? "auto" : request.Costing.Trim();
            if (!AllowedCosting.Contains(costing))
            {
                throw new ValhallaException($"Ungültiges Kostenmodell '{costing}'.", StatusCodes.Status400BadRequest);
            }

            var shapeMatch = string.IsNullOrWhiteSpace(request.ShapeMatch) ? "map_snap" : request.ShapeMatch.Trim();
            if (!AllowedShapeMatch.Contains(shapeMatch))
            {
                throw new ValhallaException($"Ungültiger shape_match-Wert '{shapeMatch}'.", StatusCodes.Status400BadRequest);
            }

            var shape = new List<MeiliShapePoint>(request.Points.Count);
            foreach (var point in request.Points)
            {
                // Koordinaten hart validieren – verrauschte, aber gültige GPS-Werte sind erlaubt,
                // unmögliche Werte werden abgewiesen.
                if (point.Latitude is < -90.0 or > 90.0 || point.Longitude is < -180.0 or > 180.0)
                {
                    throw new ValhallaException(
                        "Mindestens ein Punkt liegt außerhalb des gültigen Koordinatenbereichs.",
                        StatusCodes.Status400BadRequest);
                }

                string? type = point.Type;
                if (type is not null)
                {
                    type = type.Trim();
                    // Unbekannte Typen werden ignoriert statt weitergereicht (kein Fehler, robustes Verhalten).
                    if (!AllowedPointType.Contains(type))
                    {
                        type = null;
                    }
                }

                shape.Add(new MeiliShapePoint
                {
                    Lat = point.Latitude,
                    Lon = point.Longitude,
                    Time = point.Time,
                    Type = type
                });
            }

            MeiliTraceOptions? traceOptions = null;
            if (request.SearchRadius.HasValue || request.GpsAccuracy.HasValue || request.TurnPenaltyFactor.HasValue)
            {
                traceOptions = new MeiliTraceOptions
                {
                    // Suchradius defensiv auf den von Valhalla erlaubten Bereich begrenzen.
                    SearchRadius = request.SearchRadius.HasValue
                        ? Math.Clamp(request.SearchRadius.Value, 0, MaxSearchRadius)
                        : null,
                    GpsAccuracy = request.GpsAccuracy,
                    TurnPenaltyFactor = request.TurnPenaltyFactor
                };
            }

            return new MeiliRequest
            {
                Shape = shape,
                Costing = costing,
                ShapeMatch = shapeMatch,
                TraceOptions = traceOptions
            };
        }
        catch (ValhallaException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ValhallaException("Ungültige Map-Matching-Anfrage.", StatusCodes.Status400BadRequest, ex);
        }
    }

    /// <summary>
    /// Erstellt den Attribut-Filter: minimaler Payload für reines Live-Matching bzw. erweiterter
    /// Satz mit Kantenattributen, wenn <paramref name="includeEdges"/> gesetzt ist (Performance).
    /// </summary>
    private static MeiliFilters BuildAttributeFilter(bool includeEdges)
    {
        var attributes = new List<string>
        {
            "matched.point",
            "matched.type",
            "matched.edge_index",
            "matched.distance_along_edge",
            "matched.distance_from_trace_point",
            "shape",

            // Die Güte des Abgleichs. Ohne sie ist nicht entscheidbar, ob dem Ergebnis zu trauen
            // ist: Eine Spur mit Lücken oder starkem Rauschen wird von Meili dennoch auf Straßen
            // gelegt — nur eben auf die falschen.
            "confidence_score"
        };

        if (includeEdges)
        {
            attributes.AddRange(new[]
            {
                "edge.names",
                "edge.length",
                "edge.speed",
                "edge.speed_limit",
                "edge.road_class",
                "edge.use",
                "edge.way_id",
                "edge.begin_shape_index",
                "edge.end_shape_index"
            });
        }

        return new MeiliFilters { Attributes = attributes, Action = "include" };
    }

    /// <summary>Übersetzt die Meili-<c>trace_attributes</c>-Antwort in das öffentliche Ergebnismodell.</summary>
    private static MapMatchResult MapTraceAttributes(MapMatchRequest request, MeiliTraceAttributesResponse response)
    {
        var result = new MapMatchResult
        {
            InputPointCount = request.Points.Count,
            Shape = response.Shape,
            ConfidenceScore = response.ConfidenceScore
        };

        if (response.MatchedPoints is not null)
        {
            result.MatchedPoints = response.MatchedPoints.Select(m => new MatchedPoint
            {
                Latitude = m.Lat,
                Longitude = m.Lon,
                Type = m.Type,
                EdgeIndex = m.EdgeIndex,
                DistanceAlongEdge = m.DistanceAlongEdge,
                DistanceFromTracePoint = m.DistanceFromTracePoint
            }).ToList();
        }

        result.MatchedPointCount = result.MatchedPoints.Count;

        if (request.IncludeEdges && response.Edges is not null)
        {
            result.Edges = response.Edges.Select(e => new MatchedEdge
            {
                Names = e.Names,
                WayId = e.WayId,
                Length = e.Length,
                Speed = e.Speed,

                // Valhalla liefert hier entweder eine Zahl oder die Wortmarke "unlimited". Beides
                // wird getrennt ausgewiesen: "unbeschränkt" ist eine Aussage über die Straße,
                // "unbekannt" eine über die Datenlage.
                SpeedLimit = e.SpeedLimit.Kph,
                SpeedLimitUnlimited = e.SpeedLimit.IsUnlimited,

                RoadClass = e.RoadClass,
                Use = e.Use,
                BeginShapeIndex = e.BeginShapeIndex,
                EndShapeIndex = e.EndShapeIndex
            }).ToList();
        }

        if (request.DecodeGeometry && !string.IsNullOrEmpty(response.Shape))
        {
            result.Geometry = DecodeToGeoPoints(response.Shape);
        }

        return result;
    }

    /// <summary>Übersetzt die Meili-<c>trace_route</c>-Antwort in das öffentliche Ergebnismodell.</summary>
    private static MapRouteResult MapTraceRoute(MapMatchRequest request, MeiliTraceRouteResponse response)
    {
        var trip = response.Trip
            ?? throw new ValhallaException("Valhalla lieferte keine Route zurück.", StatusCodes.Status502BadGateway);

        var result = new MapRouteResult
        {
            LengthKm = trip.Summary?.Length ?? 0.0,
            TimeSeconds = trip.Summary?.Time ?? 0.0
        };

        if (trip.Legs is null)
        {
            return result;
        }

        foreach (var leg in trip.Legs)
        {
            if (!string.IsNullOrEmpty(leg.Shape))
            {
                result.LegShapes.Add(leg.Shape);
            }
        }

        if (request.DecodeGeometry)
        {
            var geometry = new List<GeoPoint>();
            foreach (var encoded in result.LegShapes)
            {
                geometry.AddRange(DecodeToGeoPoints(encoded));
            }

            result.Geometry = geometry;
        }

        return result;
    }

    /// <summary>
    /// Dekodiert eine von Valhalla gelieferte Encoded Polyline (Präzision 1e-6) in WGS84-Koordinaten.
    /// </summary>
    /// <remarks>
    /// Nutzt bewusst <see cref="PolylineCodec"/> und keine eigene Umsetzung. Zuvor lag hier eine
    /// zweite, wortgleiche Implementierung desselben Algorithmus. Zwei Kopien einer Kodierung sind
    /// eine Einladung dafür, dass genau eine von beiden korrigiert wird — und eine falsch dekodierte
    /// Geometrie fällt niemandem als Fehler auf, sie sieht nur etwas anders aus.
    /// </remarks>
    /// <exception cref="ValhallaException">Wenn die Polyline fehlerhaft ist.</exception>
    private static List<GeoPoint> DecodeToGeoPoints(string encoded)
    {
        try
        {
            var decoded = PolylineCodec.Decode(encoded);
            var points = new List<GeoPoint>(decoded.Count);

            foreach (var coordinate in decoded)
            {
                points.Add(new GeoPoint
                {
                    Latitude = coordinate.Latitude,
                    Longitude = coordinate.Longitude
                });
            }

            return points;
        }
        catch (FormatException ex)
        {
            throw new ValhallaException(
                "Die von Valhalla gelieferte Geometrie konnte nicht dekodiert werden.",
                StatusCodes.Status502BadGateway, ex);
        }
    }
}

// ==============================================================================================
// Meili-Wire-Modelle (interne Repräsentation der Valhalla-JSON-Verträge)
// Bewusst 'internal', damit sie nicht Teil des öffentlichen API-/Swagger-Vertrags werden.
//
// Zahlenfelder werden über die duldsamen Leser in SmartRoutingAPI.Services.Json gebunden. Grund:
// Valhalla ist ein eigenständig versioniertes Fremdsystem, dessen JSON an mehreren Stellen nicht
// typstabil ist (siehe die Erläuterung in ValhallaJsonReaders.cs). Eine strenge Bindung lässt eine
// vollständig gültige Antwort an einem einzigen Sonderfall scheitern — genau die Ursache des
// Ausfalls von /api/MapMatching/match.
// ==============================================================================================

internal sealed class MeiliRequest
{
    [JsonPropertyName("shape")]
    public List<MeiliShapePoint> Shape { get; set; } = new();

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "auto";

    [JsonPropertyName("shape_match")]
    public string ShapeMatch { get; set; } = "map_snap";

    [JsonPropertyName("trace_options")]
    public MeiliTraceOptions? TraceOptions { get; set; }

    [JsonPropertyName("filters")]
    public MeiliFilters? Filters { get; set; }
}

internal sealed class MeiliShapePoint
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("time")]
    public double? Time { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class MeiliTraceOptions
{
    [JsonPropertyName("search_radius")]
    public int? SearchRadius { get; set; }

    [JsonPropertyName("gps_accuracy")]
    public double? GpsAccuracy { get; set; }

    [JsonPropertyName("turn_penalty_factor")]
    public double? TurnPenaltyFactor { get; set; }
}

internal sealed class MeiliFilters
{
    [JsonPropertyName("attributes")]
    public List<string> Attributes { get; set; } = new();

    [JsonPropertyName("action")]
    public string Action { get; set; } = "include";
}

internal sealed class MeiliTraceAttributesResponse
{
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("matched_points")]
    public List<MeiliMatchedPoint>? MatchedPoints { get; set; }

    [JsonPropertyName("edges")]
    public List<MeiliEdge>? Edges { get; set; }

    [JsonPropertyName("units")]
    public string? Units { get; set; }

    [JsonPropertyName("confidence_score")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? ConfidenceScore { get; set; }
}

internal sealed class MeiliMatchedPoint
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Der Kantenindex. Valhalla schreibt hier einen <c>uint64</c> und lässt das Feld bei
    /// unzugeordneten Punkten weg; einzelne Fassungen geben stattdessen den Kennwert
    /// <c>kInvalidEdgeIndex</c> aus, der in kein <c>int</c> passt.
    /// </summary>
    [JsonPropertyName("edge_index")]
    [JsonConverter(typeof(TolerantNullableInt32Converter))]
    public int? EdgeIndex { get; set; }

    [JsonPropertyName("distance_along_edge")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? DistanceAlongEdge { get; set; }

    [JsonPropertyName("distance_from_trace_point")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? DistanceFromTracePoint { get; set; }
}

internal sealed class MeiliEdge
{
    [JsonPropertyName("names")]
    public List<string>? Names { get; set; }

    [JsonPropertyName("way_id")]
    [JsonConverter(typeof(TolerantNullableInt64Converter))]
    public long? WayId { get; set; }

    [JsonPropertyName("length")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? Length { get; set; }

    [JsonPropertyName("speed")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? Speed { get; set; }

    /// <summary>
    /// Die Höchstgeschwindigkeit — <b>polymorph</b>: eine Zahl in km/h oder die Zeichenkette
    /// <c>"unlimited"</c>. Dieses Feld war die Ursache des Ausfalls von
    /// <c>POST /api/MapMatching/match</c>.
    /// </summary>
    [JsonPropertyName("speed_limit")]
    [JsonConverter(typeof(ValhallaSpeedLimitConverter))]
    public ValhallaSpeedLimit SpeedLimit { get; set; } = ValhallaSpeedLimit.Unknown;

    [JsonPropertyName("road_class")]
    public string? RoadClass { get; set; }

    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("begin_shape_index")]
    [JsonConverter(typeof(TolerantNullableInt32Converter))]
    public int? BeginShapeIndex { get; set; }

    [JsonPropertyName("end_shape_index")]
    [JsonConverter(typeof(TolerantNullableInt32Converter))]
    public int? EndShapeIndex { get; set; }
}

internal sealed class MeiliTraceRouteResponse
{
    [JsonPropertyName("trip")]
    public MeiliTrip? Trip { get; set; }
}

internal sealed class MeiliTrip
{
    [JsonPropertyName("summary")]
    public MeiliSummary? Summary { get; set; }

    [JsonPropertyName("legs")]
    public List<MeiliLeg>? Legs { get; set; }

    [JsonPropertyName("units")]
    public string? Units { get; set; }
}

internal sealed class MeiliSummary
{
    [JsonPropertyName("length")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? Length { get; set; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(TolerantNullableDoubleConverter))]
    public double? Time { get; set; }
}

internal sealed class MeiliLeg
{
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("summary")]
    public MeiliSummary? Summary { get; set; }
}
