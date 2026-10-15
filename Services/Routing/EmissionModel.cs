using Microsoft.Extensions.Options;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Bewertet gefahrene Strecke und Halte in CO2e.
/// </summary>
/// <remarks>
/// Die Vorversion enthielt <em>keinerlei</em> Emissionsberechnung; die berichtete CO2-Kennzahl
/// entstand außerhalb des Systems aus einem Kilometerwert, der zudem bei mehrteiligen Routen
/// still auf <c>NULL</c> fallen konnte. Diese Schnittstelle macht die Bilanz zu einem
/// versionierten, prüfbaren Bestandteil der Anwendung.
/// </remarks>
public interface IEmissionModel
{
    /// <summary>Nachweisbare Quelle der verwendeten Faktoren. Wird mit jeder Bilanz ausgegeben.</summary>
    string FactorSource { get; }

    /// <summary>Umrechnung einer Kapazitätseinheit in Kilogramm.</summary>
    double KilogramsPerCapacityUnit { get; }

    /// <summary>CO2e in Gramm für einen Sammelhalt (Leerlauf, Aufbauhydraulik, Anfahren).</summary>
    double GramsPerStop { get; }

    /// <summary>
    /// CO2e in Gramm für eine Entleerung an einer Abladestelle.
    /// </summary>
    /// <remarks>
    /// Deutlich höher als ein Sammelhalt: Anmelden, Wiegen, Kippvorgang, Rangieren und Leerlauf
    /// dauern länger. Der Wert ist der emissionsseitige Gegenspieler zu unnötigen
    /// Zwischenentleerungen.
    /// </remarks>
    double GramsPerDisposalStop { get; }

    /// <summary>
    /// CO2e in Gramm für eine gefahrene Strecke bei bekannter Zuladung.
    /// </summary>
    /// <param name="meters">Gefahrene Strecke in Metern.</param>
    /// <param name="payloadKilograms">Zuladung während dieser Strecke in Kilogramm.</param>
    double GramsForTravel(long meters, double payloadKilograms);

    /// <summary>
    /// CO2e in Gramm für eine Strecke unter der für die <em>Zielfunktion</em> angenommenen
    /// mittleren Zuladung.
    /// </summary>
    /// <param name="meters">Gefahrene Strecke in Metern.</param>
    /// <param name="vehicleCapacityKilograms">Nutzlast des Fahrzeugs in Kilogramm.</param>
    /// <remarks>
    /// Bogenkosten in OR-Tools sind zustandsunabhängig; die echte lastabhängige Emission lässt
    /// sich dort nicht abbilden. Für die Optimierung wird daher mit einer mittleren Zuladung
    /// gerechnet, für die <b>berichtete</b> Bilanz anschließend mit dem tatsächlichen Lastverlauf
    /// der gefundenen Lösung. Diese Näherung ist bewusst gewählt und dokumentiert.
    /// </remarks>
    double GramsForTravelAtAveragePayload(long meters, double vehicleCapacityKilograms);
}

/// <summary>
/// Emissionsmodell für Abfallsammelfahrzeuge in HBEFA-Größenordnung.
/// </summary>
/// <remarks>
/// <para>Die Bilanz setzt sich aus drei Anteilen zusammen:</para>
/// <list type="number">
///   <item><description>Grundverbrauch des leeren Fahrzeugs je Kilometer.</description></item>
///   <item><description>Zuschlag je Kilometer und Kilogramm Zuladung.</description></item>
///   <item><description>Fester Anteil je Halt für Leerlauf und Schüttung.</description></item>
/// </list>
/// <para>
/// Alle drei Faktoren sind konfigurierbar und müssen vom Betreiber gegen den eigenen Fuhrpark
/// kalibriert werden. Die mitgelieferten Werte sind ausdrücklich Startwerte, kein Nachweis.
/// </para>
/// </remarks>
public sealed class HbefaRefuseTruckEmissionModel : IEmissionModel
{
    private readonly EmissionOptions _options;

    /// <summary>Erzeugt das Modell aus der Konfiguration.</summary>
    /// <param name="options">Die validierte Routing-Konfiguration.</param>
    public HbefaRefuseTruckEmissionModel(IOptions<RoutingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Emissions;
    }

    /// <inheritdoc />
    public string FactorSource => _options.FactorSource;

    /// <inheritdoc />
    public double KilogramsPerCapacityUnit => _options.PayloadUnitKilograms;

    /// <inheritdoc />
    public double GramsPerStop => _options.GramsPerStop;

    /// <inheritdoc />
    public double GramsPerDisposalStop => _options.GramsPerDisposalStop;

    /// <inheritdoc />
    public double GramsForTravel(long meters, double payloadKilograms)
    {
        try
        {
            if (meters <= 0)
            {
                return 0.0;
            }

            // Nicht befahrbare Bögen dürfen die Bilanz nicht mit dem Kennwert verfälschen.
            if (meters >= RoadMatrix.Unreachable)
            {
                return 0.0;
            }

            double kilometers = meters / 1000.0;
            double payload = Math.Max(0.0, payloadKilograms);

            return kilometers * (_options.BaseGramsPerKm + (_options.GramsPerKmPerKgPayload * payload));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die Emissionsberechnung ist fehlgeschlagen.", ex);
        }
    }

    /// <inheritdoc />
    public double GramsForTravelAtAveragePayload(long meters, double vehicleCapacityKilograms)
    {
        try
        {
            double assumedPayload = Math.Max(0.0, vehicleCapacityKilograms)
                                    * _options.AveragePayloadFactorForObjective;

            return GramsForTravel(meters, assumedPayload);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die Emissionsberechnung ist fehlgeschlagen.", ex);
        }
    }
}
