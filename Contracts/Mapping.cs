using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models.Jobs;
using SmartRoutingAPI.Models.Routes;
using SmartRoutingAPI.Models.Vehicles;
using RouteEntity = SmartRoutingAPI.Models.Routes.Route;

namespace SmartRoutingAPI.Contracts;

/// <summary>
/// Die einzige Abbildung von EF-Entitäten auf Antwort-DTOs.
/// </summary>
/// <remarks>
/// Controller bilden ausschließlich über diese Stelle ab und geben niemals EF-Entitäten zurück.
/// Alle Mitglieder sind null-sicher: nicht geladene Navigationen werden zu leeren Listen.
/// </remarks>
public static class Mapping
{
    private static readonly IReadOnlyList<SkillResponse> EmptySkills = Array.Empty<SkillResponse>();

    public static SkillResponse ToResponse(this Skill s)
        => new(s.Id, s.Name, s.Description, s.ExternalReference);

    public static IReadOnlyList<SkillResponse> ToResponse(this IEnumerable<Skill>? skills)
        => skills is null ? EmptySkills : skills.Select(s => s.ToResponse()).ToList();

    public static VehicleResponse ToResponse(this Vehicle v)
        => new(v.Id, v.Name, v.Description, v.ExternalReference, v.Capacity, v.Skills.ToResponse());

    public static JobResponse ToResponse(this Job j)
        => new(
            j.Id, j.Name, j.Description, j.ExternalReference,
            j.Capacity, j.Latitude, j.Longitude, j.Priority,
            j.Skill?.ToResponse(),
            j.ServiceTimeSeconds, j.Data);

    public static JobDataResponse ToDataResponse(this Job j) => new(j.Id, j.Data);

    public static DeliveryDestinationResponse ToResponse(this DeliveryDestination d)
        => new(
            d.Id, d.Name, d.Description, d.ExternalReference,
            d.Latitude, d.Longitude, d.MaximumCapacity, d.Skills.ToResponse(),
            d.ServiceTimeSeconds, d.ServiceSecondsPerUnit);

    public static StepResponse ToResponse(this Step s)
        => new(
            s.Sequence, s.StepType, s.JobId, s.DeliveryDestinationId,
            s.Arrival, s.TravelTimeFromStart, s.ServiceTime,
            s.DistanceFromStartMeters, s.LoadUnitsAfter,
            s.Description, s.Latitude, s.Longitude);

    public static RouteResponse ToResponse(this RouteEntity r)
    {
        // Die Halte sind die maßgebliche Darstellung und werden ausdrücklich nach Sequence
        // sortiert. Ohne ORDER BY ist die Zeilenfolge einer Datenbank unbestimmt; eine falsch
        // sortierte Schrittliste wäre für den Fahrer nicht als Fehler erkennbar.
        var orderedSteps = r.Steps is null
            ? Array.Empty<Step>()
            : r.Steps.OrderBy(s => s.Sequence).ToArray();

        return new RouteResponse(
            r.Id, r.Name, r.Description, r.ExternalReference, r.Color,
            r.StartDate, r.EndDate, r.StartLatitude, r.StartLongitude, r.EndLatitude, r.EndLongitude,
            r.Geometry, r.Meters, r.Service, r.Duration,
            r.FillShift, r.ToEmissionsResponse(),
            BuildJobOrder(orderedSteps),
            r.Jobs is null ? Array.Empty<JobResponse>() : r.Jobs.Select(j => j.ToResponse()).ToList(),
            r.Skills.ToResponse(),
            r.Vehicle?.ToResponse(),
            orderedSteps.Select(s => s.ToResponse()).ToList());
    }

    /// <summary>
    /// Leitet die Reihenfolge der bedienten Sammelpunkte aus der Schrittliste ab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zuvor war diese Reihenfolge ein <em>eigenes</em> Feld <c>Route.JobsOrderIdList</c>, das als
    /// JSON in einer <c>longtext</c>-Spalte lag. Damit existierte dieselbe Information dreifach:
    /// als Menge in <c>Route.Jobs</c>, als Reihenfolge in <c>JobsOrderIdList</c> und als Folge in
    /// <c>Route.Steps</c> – ohne jede Zusicherung, dass die drei übereinstimmen. Bei einer
    /// abgebrochenen Neuberechnung konnten sie auseinanderlaufen, und welche der drei Quellen dann
    /// die Wahrheit war, ließ sich nicht mehr feststellen.
    /// </para>
    /// <para>
    /// Die Reihenfolge wird jetzt aus der einen maßgeblichen Quelle abgeleitet. Der Vertrag nach
    /// außen bleibt unverändert (<c>jobOrder</c>), die widersprüchliche Speicherung entfällt.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<int> BuildJobOrder(IReadOnlyList<Step> orderedSteps)
    {
        var order = new List<int>(orderedSteps.Count);

        foreach (var step in orderedSteps)
        {
            if (step.StepType == StepType.Collection && step.JobId is { } jobId)
            {
                order.Add(jobId);
            }
        }

        return order;
    }

    /// <summary>
    /// Bildet die Emissionsbilanz ab und berechnet dabei die eigentliche Effizienzkennzahl
    /// „Gramm CO2e je eingesammelte Einheit“.
    /// </summary>
    /// <remarks>
    /// Die absolute CO2-Menge einer Tour ist als Kennzahl irreführend: eine längere Tour, die mehr
    /// Glas einsammelt, hat notwendigerweise mehr Ausstoß. Vergleichbar ist nur der Ausstoß je
    /// gesammelter Menge. Diese Kennzahl wird deshalb serverseitig gebildet und nicht dem Client
    /// überlassen.
    /// </remarks>
    public static EmissionsResponse ToEmissionsResponse(this RouteEntity r)
    {
        double? gramsPerUnit = r.Co2eGrams is { } grams && r.CollectedUnits is { } units && units > 0.0
            ? grams / units
            : null;

        return new EmissionsResponse(r.Co2eGrams, r.CollectedUnits, gramsPerUnit, r.EmissionFactorSource);
    }

    public static FrequencyRouteResponse ToResponse(this FrequencyRoute f)
        => new(
            f.Id, f.Name, f.Description, f.ExternalReference,
            f.StartDate, f.EndDate, f.Frequency, f.Color,
            f.StartLatitude, f.StartLongitude, f.EndLatitude, f.EndLongitude,
            f.FillShift,
            f.Skills.ToResponse(),
            f.Vehicle?.ToResponse());

    public static UserAccountResponse ToResponse(this UserAccount u)
        => new(
            u.Id, u.Username, u.Email, u.FirstName, u.LastName, u.IsActive, u.ProfilePictureUrl,
            u.CreatedAt, u.LastLogin,
            u.ApplicationRights.ToString(), u.AdministrativeRights.ToString());
}
