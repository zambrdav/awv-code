namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Wird geworfen, wenn das Straßennetz (Valhalla) keine verwertbare Antwort liefert.
/// </summary>
public sealed class RoutingEngineException : Exception
{
    /// <summary>Erzeugt eine neue <see cref="RoutingEngineException"/>.</summary>
    /// <param name="message">Für den Aufrufer geeignete, nicht sensible Fehlermeldung.</param>
    /// <param name="innerException">Optionale zugrunde liegende Ausnahme.</param>
    public RoutingEngineException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Eine vollständige Fahrzeit- und Entfernungsmatrix über eine Punktmenge, berechnet mit dem
/// konfigurierten Fahrzeug- und Sonderrechte-Profil.
/// </summary>
/// <remarks>
/// Beide Matrizen werden gemeinsam geführt, weil die Planung sie <em>gleichzeitig</em> braucht:
/// Die Entfernung liefert die CO2-Zielfunktion, die Fahrzeit die Schichtbeschränkung. Genau diese
/// Trennung war mit VROOM nicht möglich, dessen .NET-Wrapper ausschließlich eine
/// <c>durations</c>-Matrix übertragen kann.
/// </remarks>
public sealed class RoadMatrix
{
    /// <summary>
    /// Kennwert für "nicht erreichbar". Groß genug, dass der Optimierer solche Bögen niemals
    /// wählt, aber klein genug, dass Summen über tausende Bögen nicht überlaufen.
    /// </summary>
    public const long Unreachable = 1_000_000_000L;

    private readonly long[][] _seconds;
    private readonly long[][] _meters;

    /// <summary>Erzeugt eine Matrix aus vorbereiteten Zeilen.</summary>
    /// <param name="seconds">Quadratische Fahrzeitmatrix in Sekunden.</param>
    /// <param name="meters">Quadratische Entfernungsmatrix in Metern.</param>
    /// <exception cref="ArgumentException">Wenn die Matrizen nicht quadratisch oder unterschiedlich groß sind.</exception>
    public RoadMatrix(long[][] seconds, long[][] meters)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        ArgumentNullException.ThrowIfNull(meters);

        if (seconds.Length != meters.Length)
        {
            throw new ArgumentException("Fahrzeit- und Entfernungsmatrix müssen gleich groß sein.", nameof(meters));
        }

        for (int i = 0; i < seconds.Length; i++)
        {
            if (seconds[i].Length != seconds.Length || meters[i].Length != seconds.Length)
            {
                throw new ArgumentException("Die Matrizen müssen quadratisch sein.", nameof(seconds));
            }
        }

        _seconds = seconds;
        _meters = meters;
        Size = seconds.Length;
    }

    /// <summary>Kantenlänge der quadratischen Matrix.</summary>
    public int Size { get; }

    /// <summary>Fahrzeit in Sekunden von <paramref name="from"/> nach <paramref name="to"/>.</summary>
    public long Seconds(int from, int to) => _seconds[from][to];

    /// <summary>Entfernung in Metern von <paramref name="from"/> nach <paramref name="to"/>.</summary>
    public long Meters(int from, int to) => _meters[from][to];

    /// <summary>Gibt an, ob eine Verbindung überhaupt befahrbar ist.</summary>
    public bool IsReachable(int from, int to)
        => _seconds[from][to] < Unreachable && _meters[from][to] < Unreachable;

    /// <summary>
    /// Prüft, ob ein Punkt in beiden Richtungen mit dem Depot verbunden ist. Punkte ohne
    /// Verbindung müssen vor der Optimierung entfernt werden, sonst ist das Modell unlösbar.
    /// </summary>
    /// <param name="node">Der zu prüfende Knoten.</param>
    /// <param name="depot">Der Bezugsknoten (in der Regel der Schichtstart).</param>
    public bool IsConnectedTo(int node, int depot)
        => IsReachable(depot, node) && IsReachable(node, depot);
}

/// <summary>Ein Teilabschnitt einer gefahrenen Strecke zwischen zwei aufeinanderfolgenden Halten.</summary>
/// <param name="Meters">Länge des Abschnitts in Metern.</param>
/// <param name="Duration">Fahrzeit des Abschnitts.</param>
public sealed record RoadLeg(int Meters, TimeSpan Duration);

/// <summary>
/// Die tatsächlich gefahrene Strecke einer geplanten Route, berechnet mit demselben Kostenmodell
/// wie die Matrix.
/// </summary>
/// <param name="EncodedGeometry">Eine einzelne, gültige Encoded Polyline über die ganze Route.</param>
/// <param name="Meters">Gesamtlänge in Metern.</param>
/// <param name="Duration">Gesamtfahrzeit.</param>
/// <param name="Legs">Die Abschnitte zwischen den Halten, in Fahrtreihenfolge.</param>
public sealed record RoadPath(
    string EncodedGeometry,
    int Meters,
    TimeSpan Duration,
    IReadOnlyList<RoadLeg> Legs)
{
    /// <summary>Eine leere Strecke (keine oder nur ein Halt).</summary>
    public static RoadPath Empty { get; } = new(string.Empty, 0, TimeSpan.Zero, Array.Empty<RoadLeg>());
}

/// <summary>
/// Zugriff auf das Straßennetz mit dem konfigurierten Fahrzeug- und Sonderrechte-Profil.
/// </summary>
public interface IRoadNetworkService
{
    /// <summary>
    /// Berechnet die vollständige Fahrzeit- und Entfernungsmatrix über die übergebenen Punkte.
    /// </summary>
    /// <param name="points">Die Punkte in der Reihenfolge, die die Matrixindizes bestimmt.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <exception cref="RoutingEngineException">Wenn das Straßennetz keine verwertbare Antwort liefert.</exception>
    Task<RoadMatrix> GetMatrixAsync(IReadOnlyList<Coordinate> points, CancellationToken cancellationToken = default);

    /// <summary>
    /// Berechnet die gefahrene Strecke entlang einer festen Halte-Reihenfolge und liefert eine
    /// einzelne, gültige Geometrie.
    /// </summary>
    /// <param name="orderedStops">Die Halte in Fahrtreihenfolge.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <exception cref="RoutingEngineException">Wenn das Straßennetz keine verwertbare Antwort liefert.</exception>
    Task<RoadPath> GetPathAsync(IReadOnlyList<Coordinate> orderedStops, CancellationToken cancellationToken = default);

    /// <summary>Prüft, ob das Straßennetz erreichbar ist.</summary>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
