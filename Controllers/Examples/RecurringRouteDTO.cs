using Swashbuckle.AspNetCore.Filters;
using SmartRoutingAPI.Contracts;

namespace SmartRoutingAPI.Controllers.Examples;

/// <summary>Swagger example for the (relocated) <see cref="SmartRoutingAPI.Contracts.RecurringRouteDTO"/>.</summary>
public class RecurringRouteDTOExample : IExamplesProvider<RecurringRouteDTO>
{
    public RecurringRouteDTO GetExamples()
    {
        return new RecurringRouteDTO
        {
            Name = "Route 101",
            Color = 0xff0000,
            Description = "Das ist eine Beispielroute",
            Frequency = TimeSpan.FromDays(31),
            SkillsIdList = new List<int> { 1 },
            VehicleId = 1,
            StartDate = DateTimeOffset.UtcNow.Date,
            EndDate = DateTimeOffset.UtcNow.Date.AddDays(365)
        };
    }
}
