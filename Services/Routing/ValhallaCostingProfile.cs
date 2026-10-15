using Microsoft.Extensions.Options;

namespace SmartRoutingAPI.Services.Routing;

/// <summary>
/// Das <em>einzige</em> Valhalla-Kostenmodell des Systems: Fahrzeugabmessungen, Gewicht,
/// Entfernungsgewichtung und — falls freigegeben — die Sonderrechte des Hoheitlich-Modus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warum diese Klasse existiert.</b> Das Kostenmodell wurde bisher privat in
/// <see cref="ValhallaRoadNetworkService"/> aufgebaut. Sobald ein zweiter Aufrufer dieselbe
/// Kostenbasis benötigt — die Fahrer-Navigation —, ist eine private Kopie die gefährlichste
/// Lösung, die es gibt: Planung und Fahrhinweis würden auf <em>unterschiedlichen</em>
/// Kostenmodellen laufen. Der Fahrer erhielte dann eine Strecke, die der Planer nie bewertet hat,
/// und geplante gegen tatsächlich gefahrene Kilometer wären nicht mehr vergleichbar. Genau diese
/// Übereinstimmung ist im Systemdesign ausdrücklich zugesagt.
/// </para>
/// <para>
/// Die Klasse wird als Singleton registriert. Der Aufbau geschieht damit genau einmal je Prozess
/// und nicht bei jeder Erzeugung eines typisierten <c>HttpClient</c>.
/// </para>
/// <para>
/// <b>Unveränderlichkeit ist hier eine Sicherheitsanforderung, keine Stilfrage.</b> Ein Singleton,
/// dessen Wörterbuch aus mehreren Anfragen gleichzeitig serialisiert wird, darf nach dem Aufbau
/// nicht mehr verändert werden. Alle veröffentlichten Wörterbücher sind daher schreibgeschützte
/// Sichten auf beim Aufbau erzeugte, danach nie wieder berührte Instanzen.
/// </para>
/// </remarks>
public sealed class ValhallaCostingProfile
{
    /// <summary>Der Name des Valhalla-Kostenmodells, z. B. <c>truck</c>.</summary>
    public string Costing { get; }

    /// <summary>
    /// Das Kostenmodell einschließlich der konfigurierten Sonderrechte. Dies ist das Modell, mit
    /// dem geplant wird.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Planning { get; }

    /// <summary>
    /// Dasselbe Kostenmodell <em>ohne jede</em> Sonderrechte-Option — also strikt
    /// straßenverkehrsordnungskonform.
    /// </summary>
    /// <remarks>
    /// Wird von der Navigation genutzt, wenn
    /// <see cref="NavigationOptions.ApplySovereignRights"/> abgeschaltet ist. Einem <em>Menschen</em>
    /// die Einfahrt in eine Einbahnstraße gegen die Fahrtrichtung anzuweisen, ist eine andere
    /// Handlung als sie rechnerisch zu bepreisen; diese Trennung muss technisch möglich sein.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Lawful { get; }

    /// <summary>Ob überhaupt Sonderrechte konfiguriert und aktiv sind.</summary>
    public bool SovereignRightsEnabled { get; }

    /// <summary>Die dokumentierte Rechtsgrundlage der Sonderrechte; leer, wenn keine aktiv sind.</summary>
    public string SovereignLegalBasis { get; }

    /// <summary>Erzeugt das Kostenmodell aus der geprüften Routing-Konfiguration.</summary>
    /// <param name="options">Die beim Start validierte Konfiguration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> ist <c>null</c>.</exception>
    public ValhallaCostingProfile(IOptions<RoutingOptions> options)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);

            var value = options.Value
                ?? throw new ArgumentException("Die Routing-Konfiguration ist nicht gebunden.", nameof(options));

            Costing = value.Vehicle.Costing;
            SovereignRightsEnabled = value.Sovereign.Enabled;
            SovereignLegalBasis = value.Sovereign.LegalBasis;

            Lawful = Wrap(Costing, BuildVehicleCosting(value.Vehicle));
            Planning = SovereignRightsEnabled
                ? Wrap(Costing, ApplySovereignRights(BuildVehicleCosting(value.Vehicle), value.Sovereign))
                : Lawful;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ein fehlerhaftes Kostenmodell darf niemals still zu einem PKW-Profil degradieren.
            throw new InvalidOperationException(
                "Das Valhalla-Kostenmodell konnte nicht aus der Konfiguration aufgebaut werden.", ex);
        }
    }

    /// <summary>
    /// Liefert das Kostenmodell, das für einen Fahrhinweis zu verwenden ist.
    /// </summary>
    /// <param name="applySovereignRights">
    /// <c>true</c>, um dieselbe Kostenbasis wie die Planung zu verwenden (Vorgabe, verhindert das
    /// Auseinanderlaufen von Plan und Fahrhinweis); <c>false</c> für strikt StVO-konforme Hinweise.
    /// </param>
    /// <returns>Das passende Kostenmodell.</returns>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ForGuidance(bool applySovereignRights)
        => applySovereignRights ? Planning : Lawful;

    /// <summary>
    /// Baut die fahrzeugbezogenen Kostenoptionen auf.
    /// </summary>
    /// <remarks>
    /// Ohne Hüllmaße und Gewicht wertet Valhalla Brücken-, Tunnel- und Gewichtsgrenzen überhaupt
    /// nicht aus und plant faktisch einen PKW.
    /// </remarks>
    private static Dictionary<string, object> BuildVehicleCosting(VehicleProfileOptions vehicle)
        => new(StringComparer.Ordinal)
        {
            ["height"] = vehicle.HeightMeters,
            ["width"] = vehicle.WidthMeters,
            ["length"] = vehicle.LengthMeters,
            ["weight"] = vehicle.WeightTonnes,
            ["axle_load"] = vehicle.AxleLoadTonnes,
            ["axle_count"] = vehicle.AxleCount,
            ["top_speed"] = vehicle.TopSpeedKph,

            // Verschiebt die Zielfunktion von Fahrzeit hin zu Entfernung. Für eine CO2-Kennzahl
            // entscheidend: die schnellste Strecke ist regelmäßig die längere.
            ["use_distance"] = vehicle.UseDistance,

            // Die Sammlung muss Wohn- und Anliegerstraßen befahren. Der Valhalla-Standardwert
            // verbietet Straßen ohne LKW-Zufahrt praktisch vollständig.
            ["hgv_no_access_penalty"] = vehicle.HgvNoAccessPenaltySeconds
        };

    /// <summary>Ergänzt die freigegebenen Sonderrechte im übergebenen Kostenmodell.</summary>
    private static Dictionary<string, object> ApplySovereignRights(
        Dictionary<string, object> costing,
        SovereignOptions sovereign)
    {
        if (sovereign.IgnoreOneways)
        {
            costing["ignore_oneways"] = true;
        }

        if (sovereign.IgnoreNonVehicularRestrictions)
        {
            costing["ignore_non_vehicular_restrictions"] = true;
        }

        if (sovereign.IgnoreRestrictions)
        {
            costing["ignore_restrictions"] = true;
        }

        if (sovereign.IgnoreAccess)
        {
            costing["ignore_access"] = true;
        }

        return costing;
    }

    /// <summary>
    /// Verpackt die Kostenoptionen in die von Valhalla erwartete Form
    /// <c>{ "&lt;costing&gt;": { … } }</c> und friert sie ein.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Wrap(
        string costing,
        Dictionary<string, object> values)
        => new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.Ordinal)
        {
            [costing] = values
        };
}
