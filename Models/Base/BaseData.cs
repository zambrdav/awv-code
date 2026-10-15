using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SmartRoutingAPI.Models.Base;

public class BaseData
{
    [Required, MaxLength(256), Description("The required name of this data. Will be displayed.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Description { get; set; }

    [MaxLength(512), Description("An optional field to enter External References. Useful if you need some sort of 2nd identification. Maximal 512 characters.")]
    public string? ExternalReference { get; set; }
}
