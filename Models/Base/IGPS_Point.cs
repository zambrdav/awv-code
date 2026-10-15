using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Models.Base;

public interface IGPS_Point
{
    [Required]
    public double Latitude { get; set; }

    [Required]
    public double Longitude { get; set; }
}
