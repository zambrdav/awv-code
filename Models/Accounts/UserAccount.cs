using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SmartRoutingAPI.Models.Accounts;

[Flags]
public enum ApplicationRights
{
    Unknown = 0,
    ViewVehicles = 1,
    EditVehicles = 1 << 1,
    ViewRoutes = 1 << 2,
    EditRoutes = 1 << 3,
    ViewJobs = 1 << 4,
    EditJobs = 1 << 5,
    ViewSkills = 1 << 6,
    EditSkills = 1 << 7,
    ViewAdminPanel = 1 << 8,
    EditAdminPanel = 1 << 9,

    All = int.MaxValue,
}

[Flags]
public enum AdministrativeRights
{
    /// <summary>
    /// Diese Nutzer hat keine speziellen administrativen Rechte.
    /// </summary>
    None = 0,

    /// <summary>
    /// Dieser Nutzer kann neue Nutzer erstellen.
    /// </summary>
    CreateUsers = 1,

    /// <summary>
    /// Dieser Nutzer kann andere Nutzer bearbeiten.
    /// </summary>
    EditUsers = 1 << 1,

    /// <summary>
    /// Dieser Nutzer kann andere Nutzer löschen.
    /// </summary>
    DeleteUsers = 1 << 2,

    /// <summary>
    /// Diese Nutzer kann die Eigenschaften von Nutzern einsehen.
    /// </summary>
    ViewUsers = 1 << 3,

    /// <summary>
    /// Diese Nutzer kann die Eigenschaften von Nutzern einsehen.
    /// </summary>
    ViewUserActivity = 1 << 4,

    /// <summary>
    /// This user is an administrator and can do everything.
    /// </summary>
    Administrate = int.MaxValue,
}

public class UserAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = "";

    [Required]
    [MaxLength(100)]
    [EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    [MaxLength(255)]
    [JsonIgnore]
    public string PasswordHash { get; set; } = "";

    [Required]
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLogin { get; set; }

    [MaxLength(50)]
    public string FirstName { get; set; } = "";

    [MaxLength(50)]
    public string LastName { get; set; } = "";

    public bool IsActive { get; set; }

    [MaxLength(255)]
    public string ProfilePictureUrl { get; set; } = "";

    public ApplicationRights ApplicationRights { get; set; }

    public AdministrativeRights AdministrativeRights { get; set; }
}
