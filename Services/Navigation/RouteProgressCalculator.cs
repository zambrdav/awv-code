using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SmartRoutingAPI.Models.Navigation;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Services.Routing;

namespace SmartRoutingAPI.Services.Navigation;

/// <summary>
/// Berechnet, wo ein Fahrzeug auf seiner geplanten Route steht, ob es davon abgewichen ist und was
/// bis zum nächsten Halt bzw. zum Schichtende noch aussteht.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vollständig rechnerisch.</b> Kein Aufruf der Routing-Engine, kein Schreibzugriff, kein
/// Zustand. Der Dienst ist damit beliebig oft und aus beliebig vielen Instanzen aufrufbar und liegt
/// im Mikrosekundenbereich.
/// </para>
/// <para>
/// <b>Grundlage ist die gespeicherte Geometrie der Route, nicht das Straßennetz.</b> Die Frage „ist
/// dieses Fahrzeug noch auf Route X?“ ist eine Frage an <em>diese</em> Linie — nicht an OpenStreetMap.
/// Ein Abgleich gegen das Gesamtnetz (Valhalla-Meili) würde eine andere Frage beantworten,
/// zusätzliche Netzlast erzeugen und dabei ungenauer sein.
/// </para>
/// </remarks>
public sealed class RouteProgressCalculator
{
    private readonly NavigationOptions _options;

    /// <summary>
    /// Toleranz in Metern, mit der ein Halt als „noch vor dem Fahrzeug“ gilt.
    /// </summary>
    /// <remarks>
    /// Ohne diese Toleranz würde der gerade bediente Halt bei einer Positionsmeldung wenige
    /// Zentimeter dahinter sofort als erledigt gelten und die Anzeige auf den nächsten Halt
    /// springen, während der Fahrer noch am Behälter steht.
    /// </remarks>
    private const double StopAheadToleranceMeters = 5.0;

    /// <summary>Erzeugt den Rechner mit der geprüften Navigationskonfiguration.</summary>
    /// <param name="options">Die beim Start validierte Routing-Konfiguration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> ist <c>null</c>.</exception>
    public RouteProgressCalculator(IOptions<RoutingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Navigation;
    }

    /// <summary>Die wirksame Abweichungstoleranz in Metern.</summary>
    public double OffRouteToleranceMeters => _options.OffRouteToleranceMeters;

    /// <summary>Die wirksame Anzahl bestätigender Messungen vor einer Neuberechnung.</summary>
    public int ConfirmationFixesRecommended => _options.OffRouteConfirmationFixes;

    /// <summary>Der wirksame Ankunftsradius in Metern.</summary>
    public double ArrivalRadiusMeters => _options.ArrivalRadiusMeters;

    /// <summary>
    /// Bereitet die geplante Strecke einer Route für Projektionen vor.
    /// </summary>
    /// <param name="encodedGeometry">Die gespeicherte Geometrie der Route als Encoded Polyline.</param>
    /// <returns>Die vorbereitete Strecke.</returns>
    /// <exception cref="NavigationException">
    /// Die Route besitzt keine oder eine fehlerhafte Geometrie. Das ist kein Serverfehler, sondern
    /// eine Aussage über den Datenbestand: Eine Route ohne Geometrie kann nicht befahren werden und
    /// muss neu berechnet werden.
    /// </exception>
    public PlannedPath BuildPath(string? encodedGeometry)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(encodedGeometry))
            {
                throw new NavigationException(
                    "Die Route besitzt keine gespeicherte Geometrie. Sie muss neu berechnet werden, "
                    + "bevor eine Navigation möglich ist.",
                    StatusCodes.Status422UnprocessableEntity);
            }

            var path = PlannedPath.FromEncodedPolyline(encodedGeometry);

            if (!path.IsProjectable)
            {
                throw new NavigationException(
                    "Die gespeicherte Geometrie der Route ergibt keine befahrbare Strecke.",
                    StatusCodes.Status422UnprocessableEntity);
            }

            return path;
        }
        catch (NavigationException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            throw new NavigationException(
                "Die gespeicherte Geometrie der Route ist fehlerhaft und konnte nicht dekodiert werden.",
                StatusCodes.Status422UnprocessableEntity,
                ex);
        }
        catch (Exception ex)
        {
            throw new NavigationException(
                "Die geplante Strecke konnte nicht ausgewertet werden.",
                StatusCodes.Status500InternalServerError,
                ex);
        }
    }

    /// <summary>
    /// Ordnet jedem Halt seine Lage auf der geplanten Geometrie zu.
    /// </summary>
    /// <param name="path">Die vorbereitete geplante Strecke.</param>
    /// <param name="steps">Die Halte der Route; werden nach <see cref="Step.Sequence"/> sortiert.</param>
    /// <returns>Die Halte in Fahrtreihenfolge, angereichert um ihre Streckenlage.</returns>
    /// <remarks>
    /// <para>
    /// <b>Die Zuordnung ist ausdrücklich monoton.</b> Eine Sammeltour befährt dieselbe Straße
    /// mehrfach: Nach einer Zwischenentleerung führt der Weg regelmäßig über bereits gefahrene
    /// Abschnitte zurück. Ohne Monotonie würde der Lotfußpunkt eines späteren Halts auf eine
    /// <em>frühere</em> Befahrung fallen und alle daraus abgeleiteten Restdistanzen wären negativ
    /// — ein Fehler, der in der Fahreroberfläche als „minus 3 Kilometer bis zum Ziel“ auffällt,
    /// aber erst im Feld.
    /// </para>
    /// <para>
    /// Die Suche beginnt daher je Halt beim Abschnitt des vorherigen Halts, und die zurückgelegte
    /// Länge wird zusätzlich gegen den Vorgänger nach unten begrenzt.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Ein Argument ist <c>null</c>.</exception>
    public IReadOnlyList<NavigationStop> LocateStops(PlannedPath path, IEnumerable<Step>? steps)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(path);

            if (steps is null)
            {
                return Array.Empty<NavigationStop>();
            }

            var ordered = steps.OrderBy(s => s.Sequence).ToArray();
            if (ordered.Length == 0)
            {
                return Array.Empty<NavigationStop>();
            }

            if (!path.IsProjectable)
            {
                throw new NavigationException(
                    "Die gespeicherte Geometrie der Route ergibt keine befahrbare Strecke.",
                    StatusCodes.Status422UnprocessableEntity);
            }

            var located = new List<NavigationStop>(ordered.Length);
            int searchFromSegment = 0;
            double previousDistanceAlong = 0.0;

            foreach (var step in ordered)
            {
                var projection = path.Project(step.Latitude, step.Longitude, searchFromSegment);

                // Monotonie erzwingen: Zwei Halte auf demselben Abschnitt können in
                // Fahrtreihenfolge dennoch eine rückläufige relative Lage ergeben.
                double distanceAlong = Math.Max(previousDistanceAlong, projection.DistanceAlongPathMeters);

                located.Add(new NavigationStop
                {
                    Sequence = step.Sequence,
                    Type = step.StepType,
                    JobId = step.JobId,
                    DeliveryDestinationId = step.DeliveryDestinationId,
                    Name = step.Description,
                    Latitude = step.Latitude,
                    Longitude = step.Longitude,
                    PlannedArrival = step.Arrival,
                    PlannedTravelTimeFromStart = step.TravelTimeFromStart,
                    PlannedDistanceFromStartMeters = step.DistanceFromStartMeters,
                    ServiceTime = step.ServiceTime,
                    DistanceAlongRouteMeters = distanceAlong,
                    OffsetFromRouteMeters = projection.LateralOffsetMeters
                });

                searchFromSegment = projection.SegmentIndex;
                previousDistanceAlong = distanceAlong;
            }

            return located;
        }
        catch (NavigationException)
        {
            throw;
        }
        catch (ArgumentNullException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NavigationException(
                "Die Halte der Route konnten nicht auf die geplante Strecke bezogen werden.",
                StatusCodes.Status500InternalServerError,
                ex);
        }
    }

    /// <summary>
    /// Berechnet die vollständige Fortschrittsmessung eines Fahrzeugs gegen seine Route.
    /// </summary>
    /// <param name="routeId">Kennung der Route (wird gespiegelt).</param>
    /// <param name="path">Die vorbereitete geplante Strecke.</param>
    /// <param name="stops">Die bereits zugeordneten Halte aus <see cref="LocateStops"/>.</param>
    /// <param name="position">Die gemeldete Fahrzeugposition.</param>
    /// <param name="targetStopSequence">
    /// Der ausdrücklich angefahrene Halt. Ohne Angabe wird er aus der Position abgeleitet.
    /// </param>
    /// <param name="reportedMeters">Die vom Optimierer berichtete Fahrstrecke, falls vorhanden.</param>
    /// <returns>Die Fortschrittsmessung.</returns>
    /// <exception cref="ArgumentNullException">Ein Argument ist <c>null</c>.</exception>
    /// <exception cref="NavigationException">Der angegebene Halt gehört nicht zu dieser Route.</exception>
    public RouteProgressResult Calculate(
        int routeId,
        PlannedPath path,
        IReadOnlyList<NavigationStop> stops,
        NavigationPosition position,
        int? targetStopSequence,
        int? reportedMeters = null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(stops);
            ArgumentNullException.ThrowIfNull(position);

            var projection = path.Project(position.Latitude, position.Longitude);

            bool onRoute = projection.LateralOffsetMeters <= _options.OffRouteToleranceMeters;

            var result = new RouteProgressResult
            {
                RouteId = routeId,
                SnappedLatitude = projection.SnappedLatitude,
                SnappedLongitude = projection.SnappedLongitude,
                LateralOffsetMeters = projection.LateralOffsetMeters,
                ReportedAccuracyMeters = position.AccuracyMeters,
                DistanceAlongRouteMeters = projection.DistanceAlongPathMeters,
                RouteLengthMeters = path.TotalLengthMeters,
                ProgressFraction = path.TotalLengthMeters > 0.0
                    ? Math.Clamp(projection.DistanceAlongPathMeters / path.TotalLengthMeters, 0.0, 1.0)
                    : 0.0,
                ShapeIndex = projection.SegmentIndex,
                OnRoute = onRoute,
                OffRouteToleranceMeters = _options.OffRouteToleranceMeters,
                RerouteRecommended = !onRoute,
                ConfirmationFixesRecommended = _options.OffRouteConfirmationFixes,
                ArrivalRadiusMeters = _options.ArrivalRadiusMeters,
                RemainingDistanceToRouteEndMeters =
                    Math.Max(0.0, path.TotalLengthMeters - projection.DistanceAlongPathMeters),
                ReportedMeters = reportedMeters
            };

            if (stops.Count == 0)
            {
                return result;
            }

            int targetIndex = ResolveTargetIndex(stops, projection.DistanceAlongPathMeters, targetStopSequence);
            result.NextStopWasInferred = targetStopSequence is null;

            if (targetIndex >= 0)
            {
                var target = stops[targetIndex];

                result.NextStop = target;
                result.RemainingDistanceToNextStopMeters =
                    Math.Max(0.0, target.DistanceAlongRouteMeters - projection.DistanceAlongPathMeters);
                result.RemainingTravelTimeToNextStop =
                    RemainingTravelTime(stops, projection.DistanceAlongPathMeters, targetIndex);

                double straightLine = PlannedPath.StraightLineDistanceMeters(
                    position.Latitude, position.Longitude, target.Latitude, target.Longitude);

                result.StraightLineDistanceToNextStopMeters = straightLine;
                result.AtNextStop = straightLine <= _options.ArrivalRadiusMeters;
            }

            result.RemainingTravelTimeToRouteEnd =
                RemainingTravelTime(stops, projection.DistanceAlongPathMeters, stops.Count - 1);

            var remaining = new List<NavigationStop>();
            var remainingService = TimeSpan.Zero;

            foreach (var stop in stops)
            {
                if (stop.DistanceAlongRouteMeters + StopAheadToleranceMeters
                    < projection.DistanceAlongPathMeters)
                {
                    continue;
                }

                remaining.Add(stop);
                remainingService += stop.ServiceTime;
            }

            result.RemainingStops = remaining;
            result.RemainingServiceTimeToRouteEnd = remainingService;

            return result;
        }
        catch (NavigationException)
        {
            throw;
        }
        catch (ArgumentNullException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NavigationException(
                "Der Fortschritt auf der Route konnte nicht berechnet werden.",
                StatusCodes.Status500InternalServerError,
                ex);
        }
    }

    /// <summary>
    /// Bestimmt den Index des angefahrenen Halts.
    /// </summary>
    /// <returns>Der Index in <paramref name="stops"/>, oder -1, wenn kein Halt mehr aussteht.</returns>
    /// <exception cref="NavigationException">
    /// Ein ausdrücklich benannter Halt gehört nicht zu dieser Route. Ein solcher Verweis wird
    /// abgewiesen und nicht stillschweigend durch einen abgeleiteten Halt ersetzt: Der Fahrer würde
    /// sonst zu einem anderen Ziel geführt als von der Anwendung angefordert.
    /// </exception>
    private static int ResolveTargetIndex(
        IReadOnlyList<NavigationStop> stops,
        double distanceAlongMeters,
        int? targetStopSequence)
    {
        if (targetStopSequence is { } sequence)
        {
            for (int i = 0; i < stops.Count; i++)
            {
                if (stops[i].Sequence == sequence)
                {
                    return i;
                }
            }

            throw new NavigationException(
                $"Die Route enthält keinen Halt mit der Position {sequence}.",
                StatusCodes.Status400BadRequest);
        }

        // Abgeleitet: der erste Halt, der noch vor dem Fahrzeug liegt. Der Schichtstart (Position 0)
        // ist dabei kein Ziel — er liegt bauartbedingt am Streckenbeginn hinter dem Fahrzeug.
        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];

            if (stop.Type == StepType.Start)
            {
                continue;
            }

            if (stop.DistanceAlongRouteMeters > distanceAlongMeters + StopAheadToleranceMeters)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Summiert die geplante Restfahrzeit von der aktuellen Streckenlage bis zu einem Halt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Innerhalb eines Abschnitts zwischen zwei Halten wird <em>linear</em> nach der Streckenlänge
    /// interpoliert. Das ist eine bewusste Näherung: Der Plan enthält nur die Fahrzeit je Abschnitt,
    /// nicht deren Verlauf. Die gemeldete Zahl ist damit die <b>geplante</b> Restfahrzeit und
    /// ausdrücklich keine Vorhersage aus der tatsächlich gefahrenen Geschwindigkeit.
    /// </para>
    /// <para>
    /// Bereits vollständig zurückgelegte Abschnitte tragen null bei, noch nicht begonnene tragen
    /// vollständig bei. Dadurch bleibt das Ergebnis auch dann richtig, wenn das Fahrzeug mehrere
    /// Halte hinter dem angefragten Ziel liegt.
    /// </para>
    /// </remarks>
    private static TimeSpan RemainingTravelTime(
        IReadOnlyList<NavigationStop> stops,
        double distanceAlongMeters,
        int uptoIndex)
    {
        var remaining = TimeSpan.Zero;

        for (int i = 1; i <= uptoIndex && i < stops.Count; i++)
        {
            var from = stops[i - 1];
            var to = stops[i];

            if (to.DistanceAlongRouteMeters <= distanceAlongMeters)
            {
                continue;
            }

            var legTime = to.PlannedTravelTimeFromStart - from.PlannedTravelTimeFromStart;
            if (legTime <= TimeSpan.Zero)
            {
                continue;
            }

            double legLength = to.DistanceAlongRouteMeters - from.DistanceAlongRouteMeters;
            double covered = Math.Max(0.0, distanceAlongMeters - from.DistanceAlongRouteMeters);

            double remainingFraction = legLength > 0.0
                ? Math.Clamp(1.0 - (covered / legLength), 0.0, 1.0)
                : 1.0;

            remaining += legTime * remainingFraction;
        }

        return remaining;
    }
}
