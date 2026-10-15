using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SmartRoutingAPI.Services;

/// <summary>
/// Der gemeinsame Aufrufweg zu einer Valhalla-Instanz: senden, Antwort puffern, Fehler klassifizieren
/// und Deserialisierungsfehler <em>diagnostizierbar</em> machen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warum das zusammengefasst ist.</b> Map-Matching und Fahrer-Navigation sprechen dieselbe
/// Instanz mit denselben Fehlerbildern. Zwei Kopien dieser Behandlung würden zwangsläufig
/// auseinanderlaufen — und zwar an der Stelle, die im Fehlerfall als einzige weiterhilft.
/// </para>
/// <para>
/// <b>Warum die Antwort gepuffert wird.</b> Ein Deserialisierungsfehler ohne die Angabe, <em>welches
/// Feld</em> ihn ausgelöst hat, ist praktisch nicht behebbar. Genau das war der Zustand beim Ausfall
/// von <c>/api/MapMatching/match</c>: Das Protokoll enthielt nur „Antwort konnte nicht
/// deserialisiert werden“, und das schuldige Feld — ein <c>speed_limit</c> mit dem Wert
/// <c>"unlimited"</c> — war aus der Meldung nicht ableitbar. Die Antwort wird deshalb einmal in
/// einen Bytepuffer gelesen; daraus liefert <see cref="JsonException.Path"/> den exakten JSON-Pfad,
/// und ein begrenzter Auszug zeigt den Rohwert.
/// </para>
/// </remarks>
internal static class ValhallaHttpExchange
{
    /// <summary>
    /// Obergrenze der gepufferten Antwortgröße in Byte.
    /// </summary>
    /// <remarks>
    /// Eine fehlerhafte oder übernommene Routing-Instanz darf diesen Prozess nicht über den
    /// Speicher zu Fall bringen. 64 MiB liegen weit über jeder plausiblen Antwort — eine
    /// <c>trace_attributes</c>-Antwort mit 5000 Punkten und allen Kantenattributen bleibt im
    /// einstelligen Megabytebereich.
    /// </remarks>
    private const int MaxBufferedResponseBytes = 64 * 1024 * 1024;

    /// <summary>Zeichenzahl des Auszugs, der bei einem Deserialisierungsfehler protokolliert wird.</summary>
    private const int DiagnosticExcerptBytes = 240;

    /// <summary>
    /// Sendet eine Anfrage an einen Valhalla-Endpunkt und liefert die deserialisierte Antwort.
    /// </summary>
    /// <typeparam name="TResponse">Der erwartete Antworttyp.</typeparam>
    /// <param name="http">Der typisierte, gepoolte HTTP-Client mit gesetzter Basisadresse.</param>
    /// <param name="path">Der Pfad des Valhalla-Endpunkts, z. B. <c>/route</c>.</param>
    /// <param name="body">Der zu sendende Anfragekörper.</param>
    /// <param name="serializerOptions">Die Serialisierungseinstellungen beider Richtungen.</param>
    /// <param name="logger">Logger für die serverseitige Diagnose.</param>
    /// <param name="cancellationToken">Token zum Abbrechen (Client-Trennung).</param>
    /// <returns>Die deserialisierte Antwort.</returns>
    /// <exception cref="ValhallaException">
    /// Bei jedem Transport-, Protokoll- oder Formatfehler, jeweils mit passendem Statuscode.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Wenn <paramref name="cancellationToken"/> ausgelöst wurde. Wird ausdrücklich nicht in eine
    /// <see cref="ValhallaException"/> übersetzt: Ein Client-Abbruch ist kein Fehler.
    /// </exception>
    public static async Task<TResponse> PostAsync<TResponse>(
        HttpClient http,
        string path,
        object body,
        JsonSerializerOptions serializerOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(logger);

        HttpResponseMessage response;
        try
        {
            response = await http
                .PostAsJsonAsync(path, body, serializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Der Aufrufer hat abgebrochen. Weiterreichen, nicht als Serverfehler ausgeben.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Kein Abbruch durch den Aufrufer => es war das Timeout des HttpClient.
            logger.LogWarning(ex, "Zeitüberschreitung bei der Valhalla-Anfrage an {Path}.", path);
            throw new ValhallaException(
                "Zeitüberschreitung bei der Anfrage an die Valhalla-Instanz.",
                StatusCodes.Status504GatewayTimeout, ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Die Valhalla-Instanz ist nicht erreichbar (Pfad {Path}).", path);
            throw new ValhallaException(
                "Die Valhalla-Instanz ist derzeit nicht erreichbar.",
                StatusCodes.Status502BadGateway, ex);
        }

        using (response)
        {
            byte[] payload = await ReadBoundedAsync(response, path, logger, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw BuildUpstreamFailure(response, path, payload, serializerOptions, logger);
            }

            try
            {
                var result = JsonSerializer.Deserialize<TResponse>(payload, serializerOptions);

                if (result is null)
                {
                    throw new ValhallaException(
                        "Leere Antwort der Valhalla-Instanz.",
                        StatusCodes.Status502BadGateway);
                }

                return result;
            }
            catch (JsonException ex)
            {
                // Der eigentliche Zweck dieser Klasse: Ohne Pfad und Rohwert ist ein Schemaversatz
                // eines Fremdsystems nicht auffindbar.
                logger.LogError(
                    ex,
                    "Die Antwort der Valhalla-Instanz auf {Path} passt nicht zum erwarteten Schema. "
                    + "JSON-Pfad: {JsonPath}. Zeile {Line}, Byte {BytePosition}. Auszug: {Excerpt}",
                    path,
                    ex.Path ?? "(unbekannt)",
                    ex.LineNumber ?? -1,
                    ex.BytePositionInLine ?? -1,
                    BuildExcerpt(payload, ex.BytePositionInLine));

                throw new ValhallaException(
                    string.IsNullOrEmpty(ex.Path)
                        ? "Die Antwort der Valhalla-Instanz konnte nicht verarbeitet werden."
                        : $"Die Antwort der Valhalla-Instanz konnte nicht verarbeitet werden (Feld {ex.Path}).",
                    StatusCodes.Status502BadGateway,
                    ex);
            }
        }
    }

    /// <summary>
    /// Liest den Antwortkörper vollständig, aber mit einer harten Obergrenze.
    /// </summary>
    /// <exception cref="ValhallaException">Die Antwort überschreitet die Obergrenze.</exception>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        string path,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        long? declaredLength = response.Content.Headers.ContentLength;

        if (declaredLength > MaxBufferedResponseBytes)
        {
            logger.LogError(
                "Die Valhalla-Antwort auf {Path} ist mit {Bytes} Byte unplausibel groß und wurde abgewiesen.",
                path, declaredLength);

            throw new ValhallaException(
                "Die Antwort der Valhalla-Instanz ist unplausibel groß.",
                StatusCodes.Status502BadGateway);
        }

        try
        {
            byte[] payload = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            if (payload.Length > MaxBufferedResponseBytes)
            {
                throw new ValhallaException(
                    "Die Antwort der Valhalla-Instanz ist unplausibel groß.",
                    StatusCodes.Status502BadGateway);
            }

            return payload;
        }
        catch (ValhallaException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Der Antwortkörper der Valhalla-Instanz ({Path}) konnte nicht gelesen werden.", path);
            throw new ValhallaException(
                "Die Antwort der Valhalla-Instanz konnte nicht gelesen werden.",
                StatusCodes.Status502BadGateway, ex);
        }
    }

    /// <summary>
    /// Übersetzt eine Fehlerantwort der Valhalla-Instanz in eine <see cref="ValhallaException"/>.
    /// </summary>
    /// <remarks>
    /// Ein <c>400</c> von Valhalla ist keine Fehlfunktion der Instanz, sondern die Aussage „mit
    /// diesen Eingaben ist kein Ergebnis möglich“ — etwa eine nicht abgleichbare Spur oder eine
    /// Koordinate, die auf keiner für dieses Fahrzeug befahrbaren Straße liegt. Das wird zu
    /// <c>422</c>, damit der Aufrufer „Daten nicht verwertbar“ von „Instanz defekt“ unterscheiden
    /// kann. Die Meldung von Valhalla wird dabei weitergegeben: Sie benennt die Ursache genauer, als
    /// diese API es könnte, und enthält keine fremden Daten.
    /// </remarks>
    private static ValhallaException BuildUpstreamFailure(
        HttpResponseMessage response,
        string path,
        byte[] payload,
        JsonSerializerOptions serializerOptions,
        ILogger logger)
    {
        string? upstreamMessage = TryReadErrorMessage(payload, serializerOptions);

        logger.LogInformation(
            "Die Valhalla-Instanz antwortete auf {Path} mit {StatusCode}: {Message}",
            path, (int)response.StatusCode, upstreamMessage ?? "(keine Meldung)");

        int status = response.StatusCode == System.Net.HttpStatusCode.BadRequest
            ? StatusCodes.Status422UnprocessableEntity
            : StatusCodes.Status502BadGateway;

        return new ValhallaException(
            upstreamMessage ?? $"Valhalla-Fehler ({(int)response.StatusCode}).",
            status);
    }

    /// <summary>
    /// Versucht, die Fehlermeldung aus einer Valhalla-Fehlerantwort zu lesen. Schlägt das fehl, wird
    /// <c>null</c> geliefert; Rohinhalte werden nie an den Aufrufer weitergegeben.
    /// </summary>
    private static string? TryReadErrorMessage(byte[] payload, JsonSerializerOptions serializerOptions)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ValhallaErrorPayload>(payload, serializerOptions);
            return string.IsNullOrWhiteSpace(error?.Error) ? null : error!.Error;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Erzeugt einen begrenzten, um die Fehlerstelle zentrierten Auszug des Antwortkörpers.
    /// </summary>
    /// <remarks>
    /// Valhalla schreibt seine Antworten einzeilig; <see cref="JsonException.BytePositionInLine"/>
    /// ist damit praktisch der absolute Byteoffset und der Auszug trifft die schuldige Stelle. Die
    /// Länge ist bewusst hart begrenzt, damit ein Protokolleintrag nicht mehrere Megabyte umfasst.
    /// </remarks>
    private static string BuildExcerpt(byte[] payload, long? bytePosition)
    {
        try
        {
            if (payload.Length == 0)
            {
                return "(leer)";
            }

            int center = bytePosition is { } position && position >= 0 && position < payload.Length
                ? (int)position
                : 0;

            int start = Math.Max(0, center - (DiagnosticExcerptBytes / 2));
            int length = Math.Min(DiagnosticExcerptBytes, payload.Length - start);

            return Encoding.UTF8.GetString(payload, start, length);
        }
        catch (Exception)
        {
            // Die Diagnose darf die Fehlerbehandlung niemals selbst zum Scheitern bringen.
            return "(Auszug nicht darstellbar)";
        }
    }

    /// <summary>Die Fehlerhülle, die Valhalla bei einem Fehlschlag liefert.</summary>
    private sealed class ValhallaErrorPayload
    {
        /// <summary>Die Klartextmeldung von Valhalla.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>Valhallas eigener Fehlercode.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("error_code")]
        public int ErrorCode { get; set; }
    }
}
