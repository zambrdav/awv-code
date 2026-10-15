using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// <see cref="IRoadNetworkService"/> auf Basis einer selbst gehosteten Valhalla-Instanz.
/// </summary>
/// <remarks>
/// <para>
/// Diese Klasse ist die Stelle, an der der <b>Hoheitlich-Modus</b> tatsÃ¤chlich wirksam wird.
/// Valhalla unterstÃ¼tzt die dafÃ¼r nÃ¶tigen Kostenoptionen von Haus aus
/// (<c>ignore_oneways</c>, <c>ignore_non_vehicular_restrictions</c>, <c>ignore_restrictions</c>,
/// <c>ignore_access</c>) sowie ein echtes LKW-Kostenmodell mit Abmessungen und Gewicht. In der
/// Vorversion wurde Valhalla ausschlieÃŸlich fÃ¼r Map-Matching genutzt, wÃ¤hrend die Routenplanung
/// Ã¼ber ein PKW-Profil lief â€“ die Sonderrechte waren dadurch nirgends abgebildet.
/// </para>
/// <para>
/// Es werden zwei Endpunkte genutzt: <c>/sources_to_targets</c> fÃ¼r die Matrix und <c>/route</c>
/// fÃ¼r die Geometrie der festgelegten Reihenfolge. Beide verwenden <em>dasselbe</em> Kostenmodell,
/// damit geplante und berichtete Kilometer nicht auseinanderlaufen.
/// </para>
/// </remarks>
public sealed class ValhallaRoadNetworkService : IRoadNetworkService
{
    private readonly HttpClient _http;
    private readonly RoutingOptions _options;
    private readonly ILogger<ValhallaRoadNetworkService> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Das gemeinsame Kostenmodell von Planung und Fahrer-Navigation.
    /// </summary>
    /// <remarks>
    /// Zuvor wurde das Modell hier privat aufgebaut. Mit dem Hinzukommen der Fahrer-Navigation als
    /// zweitem Aufrufer war eine private Kopie nicht mehr vertretbar: Planung und Fahrhinweis mÃ¼ssen
    /// nachweislich dieselbe Kostenbasis verwenden, sonst fÃ¼hrt die Navigation Ã¼ber StraÃŸen, die der
    /// Planer nie bewertet hat. Siehe <see cref="ValhallaCostingProfile"/>.
    /// </remarks>
    private readonly ValhallaCostingProfile _costingProfile;

    /// <summary>Erzeugt den Dienst.</summary>
    /// <param name="http">Gepoolter, typisierter HTTP-Client (Basisadresse und Timeout via DI gesetzt).</param>
    /// <param name="costingProfile">Das gemeinsame, einmal je Prozess aufgebaute Kostenmodell.</param>
    /// <param name="options">Die validierte Routing-Konfiguration.</param>
    /// <param name="logger">Logger fÃ¼r Diagnose und Sonderrechte-Protokollierung.</param>
    public ValhallaRoadNetworkService(
        HttpClient http,
        ValhallaCostingProfile costingProfile,
        IOptions<RoutingOptions> options,
        ILogger<ValhallaRoadNetworkService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = http ?? throw new ArgumentNullException(nameof(http));
        _costingProfile = costingProfile ?? throw new ArgumentNullException(nameof(costingProfile));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_options.Sovereign.Enabled)
        {
            // Sonderrechte sind eine bewusste Abweichung von der StVO und mÃ¼ssen nachweisbar sein.
            _logger.LogInformation(
                "Hoheitlich-Modus aktiv. Rechtsgrundlage: {LegalBasis}. EinbahnstraÃŸen ignorieren: {Oneways}, "
                + "BeschrÃ¤nkungen ohne Sicherheitsrelevanz ignorieren: {NonVehicular}, alle BeschrÃ¤nkungen ignorieren: {All}, "
                + "ZufahrtsbeschrÃ¤nkungen ignorieren: {Access}.",
                _options.Sovereign.LegalBasis,
                _options.Sovereign.IgnoreOneways,
                _options.Sovereign.IgnoreNonVehicularRestrictions,
                _options.Sovereign.IgnoreRestrictions,
                _options.Sovereign.IgnoreAccess);
        }
    }

    /// <inheritdoc />
    public async Task<RoadMatrix> GetMatrixAsync(
        IReadOnlyList<Coordinate> points,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(points);

            if (points.Count == 0)
            {
                throw new RoutingEngineException("FÃ¼r eine Matrix wird mindestens ein Punkt benÃ¶tigt.");
            }

            int size = points.Count;
            var seconds = CreateFilledMatrix(size);
            var meters = CreateFilledMatrix(size);

            // Die Diagonale ist definitionsgemÃ¤ÃŸ null und wird nie abgefragt.
            for (int i = 0; i < size; i++)
            {
                seconds[i][i] = 0;
                meters[i][i] = 0;
            }

            int block = Math.Max(2, _options.Valhalla.MaxMatrixLocationsPerRequest);

            for (int sourceStart = 0; sourceStart < size; sourceStart += block)
            {
                int sourceCount = Math.Min(block, size - sourceStart);

                for (int targetStart = 0; targetStart < size; targetStart += block)
                {
                    int targetCount = Math.Min(block, size - targetStart);

                    await FillMatrixBlockAsync(
                            points, seconds, meters,
                            sourceStart, sourceCount, targetStart, targetCount,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return new RoadMatrix(seconds, meters);
        }
        catch (RoutingEngineException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler beim Aufbau der Entfernungsmatrix.");
            throw new RoutingEngineException("Die Entfernungsmatrix konnte nicht berechnet werden.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<RoadPath> GetPathAsync(
        IReadOnlyList<Coordinate> orderedStops,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(orderedStops);

            if (orderedStops.Count < 2)
            {
                // Eine Route mit weniger als zwei Halten hat keine Geometrie â€“ das ist kein Fehler.
                return RoadPath.Empty;
            }

            // Valhalla begrenzt die StÃ¼tzpunkte je /route-Aufruf. LÃ¤ngere Routen werden in
            // Ã¼berlappende Abschnitte zerlegt: der letzte Halt eines Blocks ist der erste des
            // nÃ¤chsten, damit keine LÃ¼cke in der Geometrie entsteht.
            int maxPerRequest = Math.Max(2, _options.Valhalla.MaxRouteLocationsPerRequest);

            var shapes = new List<string?>();
            var legs = new List<RoadLeg>();
            int totalMeters = 0;
            double totalSeconds = 0.0;

            int index = 0;
            while (index < orderedStops.Count - 1)
            {
                int count = Math.Min(maxPerRequest, orderedStops.Count - index);
                var chunk = new List<Coordinate>(count);
                for (int i = 0; i < count; i++)
                {
                    chunk.Add(orderedStops[index + i]);
                }

                var trip = await RequestRouteAsync(chunk, cancellationToken).ConfigureAwait(false);

                if (trip.Legs is not null)
                {
                    foreach (var leg in trip.Legs)
                    {
                        shapes.Add(leg.Shape);

                        int legMeters = ToMeters(leg.Summary?.Length);
                        double legSeconds = leg.Summary?.Time ?? 0.0;

                        legs.Add(new RoadLeg(legMeters, TimeSpan.FromSeconds(legSeconds)));
                        totalMeters += legMeters;
                        totalSeconds += legSeconds;
                    }
                }

                // Der letzte Halt dieses Blocks ist der Startpunkt des nÃ¤chsten.
                index += count - 1;
            }

            return new RoadPath(
                PolylineCodec.Stitch(shapes),
                totalMeters,
                TimeSpan.FromSeconds(totalSeconds),
                legs);
        }
        catch (RoutingEngineException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei der Streckenberechnung.");
            throw new RoutingEngineException("Die Strecke konnte nicht berechnet werden.", ex);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Eine ErreichbarkeitsprÃ¼fung darf nie werfen; sie liefert bewusst nur true/false.
            _logger.LogWarning(ex, "Valhalla ist derzeit nicht erreichbar.");
            return false;
        }
    }

    // ==========================================================================================
    // Interne Hilfsmethoden
    // ==========================================================================================

    /// <summary>Holt einen rechteckigen Block der Matrix und schreibt ihn an die richtige Stelle.</summary>
    private async Task FillMatrixBlockAsync(
        IReadOnlyList<Coordinate> points,
        long[][] seconds,
        long[][] meters,
        int sourceStart,
        int sourceCount,
        int targetStart,
        int targetCount,
        CancellationToken cancellationToken)
    {
        var request = new ValhallaMatrixRequest
        {
            Sources = Slice(points, sourceStart, sourceCount),
            Targets = Slice(points, targetStart, targetCount),
            Costing = _costingProfile.Costing,
            CostingOptions = _costingProfile.Planning,
            Units = "kilometers"
        };

        var response = await PostAsync<ValhallaMatrixResponse>("/sources_to_targets", request, cancellationToken)
            .ConfigureAwait(false);

        if (response.SourcesToTargets is null)
        {
            throw new RoutingEngineException("Valhalla lieferte keine Matrix zurÃ¼ck.");
        }

        for (int row = 0; row < response.SourcesToTargets.Count; row++)
        {
            var cells = response.SourcesToTargets[row];
            if (cells is null)
            {
                continue;
            }

            for (int column = 0; column < cells.Count; column++)
            {
                var cell = cells[column];

                // Die Indizes aus der Antwort sind maÃŸgeblich, wenn vorhanden; sonst die Position.
                int localFrom = cell.FromIndex ?? row;
                int localTo = cell.ToIndex ?? column;

                int globalFrom = sourceStart + localFrom;
                int globalTo = targetStart + localTo;

                if (globalFrom >= seconds.Length || globalTo >= seconds.Length)
                {
                    continue;
                }

                if (globalFrom == globalTo)
                {
                    continue;
                }

                // Fehlende Werte bedeuten "nicht erreichbar" und bleiben auf dem Kennwert stehen.
                if (cell.Time is null || cell.Distance is null)
                {
                    continue;
                }

                seconds[globalFrom][globalTo] = (long)Math.Round(cell.Time.Value, MidpointRounding.AwayFromZero);
                meters[globalFrom][globalTo] = ToMeters(cell.Distance);
            }
        }
    }

    /// <summary>FÃ¼hrt einen <c>/route</c>-Aufruf fÃ¼r einen Abschnitt der Halte-Reihenfolge aus.</summary>
    private async Task<ValhallaTrip> RequestRouteAsync(
        IReadOnlyList<Coordinate> stops,
        CancellationToken cancellationToken)
    {
        var request = new ValhallaRouteRequest
        {
            Locations = stops
                .Select(s => new ValhallaLocation { Lat = s.Latitude, Lon = s.Longitude, Type = "break" })
                .ToList(),
            Costing = _costingProfile.Costing,
            CostingOptions = _costingProfile.Planning,
            Units = "kilometers",

            // FÃ¼r die Planung werden ausschlieÃŸlich Geometrie und Kennzahlen benÃ¶tigt;
            // Abbiegehinweise wÃ¼rden die Antwort um ein Vielfaches vergrÃ¶ÃŸern.
            //
            // Abbiegehinweise fÃ¼r den Fahrer entstehen deshalb NICHT hier, sondern in
            // SmartRoutingAPI.Services.Navigation.ValhallaNavigationService â€“ dort mit
            // directions_type = "instructions", aber mit genau demselben Kostenmodell.
            DirectionsType = "none"
        };

        var response = await PostAsync<ValhallaRouteResponse>("/route", request, cancellationToken)
            .ConfigureAwait(false);

        return response.Trip
            ?? throw new RoutingEngineException("Valhalla lieferte keine Route zurÃ¼ck.");
    }

    /// <summary>Sendet eine Anfrage und Ã¼bersetzt Transportfehler in <see cref="RoutingEngineException"/>.</summary>
    private async Task<TResponse> PostAsync<TResponse>(
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ZeitÃ¼berschreitung bei Valhalla-Anfrage an {Path}.", path);
            throw new RoutingEngineException("ZeitÃ¼berschreitung bei der Anfrage an Valhalla.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Valhalla ist nicht erreichbar (Pfad {Path}).", path);
            throw new RoutingEngineException("Valhalla ist derzeit nicht erreichbar.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? detail = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Valhalla antwortete auf {Path} mit {StatusCode}: {Detail}",
                    path, (int)response.StatusCode, detail);

                throw new RoutingEngineException(
                    detail is null
                        ? $"Valhalla-Fehler ({(int)response.StatusCode}) bei {path}."
                        : $"Valhalla-Fehler bei {path}: {detail}");
            }

            try
            {
                var result = await response.Content
                    .ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                return result ?? throw new RoutingEngineException("Valhalla lieferte eine leere Antwort.");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Die Antwort von Valhalla konnte nicht gelesen werden ({Path}).", path);
                throw new RoutingEngineException("Die Antwort von Valhalla konnte nicht verarbeitet werden.", ex);
            }
        }
    }

    /// <summary>Liest die Fehlermeldung aus einer Valhalla-Fehlerantwort, wenn mÃ¶glich.</summary>
    private async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ValhallaError>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(error?.Error) ? null : error!.Error;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Die Fehlerantwort von Valhalla konnte nicht gelesen werden.");
            return null;
        }
    }

    /// <summary>Erzeugt eine quadratische Matrix, vorbelegt mit dem "nicht erreichbar"-Kennwert.</summary>
    private static long[][] CreateFilledMatrix(int size)
    {
        var matrix = new long[size][];
        for (int i = 0; i < size; i++)
        {
            matrix[i] = new long[size];
            Array.Fill(matrix[i], RoadMatrix.Unreachable);
        }

        return matrix;
    }

    /// <summary>Schneidet einen Abschnitt der Punktliste als Valhalla-Positionen heraus.</summary>
    private static List<ValhallaLocation> Slice(IReadOnlyList<Coordinate> points, int start, int count)
    {
        var slice = new List<ValhallaLocation>(count);
        for (int i = 0; i < count; i++)
        {
            var point = points[start + i];
            slice.Add(new ValhallaLocation { Lat = point.Latitude, Lon = point.Longitude });
        }

        return slice;
    }

    /// <summary>
    /// Rechnet eine Valhalla-LÃ¤nge (angefordert in Kilometern) in ganze Meter um.
    /// </summary>
    private static int ToMeters(double? kilometers)
    {
        if (kilometers is null || double.IsNaN(kilometers.Value) || double.IsInfinity(kilometers.Value))
        {
            return 0;
        }

        double meters = Math.Round(kilometers.Value * 1000.0, MidpointRounding.AwayFromZero);

        // Absicherung gegen unplausible Werte, damit nie ein Ãœberlauf in die Matrix gelangt.
        return meters >= int.MaxValue ? int.MaxValue : (int)Math.Max(0.0, meters);
    }
}

// ==============================================================================================
// Valhalla-Wire-Modelle. Bewusst 'internal': sie sind kein Teil des Ã¶ffentlichen API-Vertrags.
// ==============================================================================================

internal sealed class ValhallaLocation
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class ValhallaMatrixRequest
{
    [JsonPropertyName("sources")]
    public List<ValhallaLocation> Sources { get; set; } = new();

    [JsonPropertyName("targets")]
    public List<ValhallaLocation> Targets { get; set; } = new();

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "truck";

    [JsonPropertyName("costing_options")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? CostingOptions { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = "kilometers";
}

internal sealed class ValhallaMatrixResponse
{
    [JsonPropertyName("sources_to_targets")]
    public List<List<ValhallaMatrixCell>?>? SourcesToTargets { get; set; }
}

internal sealed class ValhallaMatrixCell
{
    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    [JsonPropertyName("time")]
    public double? Time { get; set; }

    [JsonPropertyName("from_index")]
    public int? FromIndex { get; set; }

    [JsonPropertyName("to_index")]
    public int? ToIndex { get; set; }
}

internal sealed class ValhallaRouteRequest
{
    [JsonPropertyName("locations")]
    public List<ValhallaLocation> Locations { get; set; } = new();

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "truck";

    [JsonPropertyName("costing_options")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? CostingOptions { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = "kilometers";

    [JsonPropertyName("directions_type")]
    public string? DirectionsType { get; set; }
}

internal sealed class ValhallaRouteResponse
{
    [JsonPropertyName("trip")]
    public ValhallaTrip? Trip { get; set; }
}

internal sealed class ValhallaTrip
{
    [JsonPropertyName("legs")]
    public List<ValhallaTripLeg>? Legs { get; set; }

    [JsonPropertyName("summary")]
    public ValhallaTripSummary? Summary { get; set; }
}

internal sealed class ValhallaTripLeg
{
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("summary")]
    public ValhallaTripSummary? Summary { get; set; }
}

internal sealed class ValhallaTripSummary
{
    [JsonPropertyName("length")]
    public double? Length { get; set; }

    [JsonPropertyName("time")]
    public double? Time { get; set; }
}

internal sealed class ValhallaError
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
