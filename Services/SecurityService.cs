using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using SmartRoutingAPI.Models.Accounts;

namespace SmartRoutingAPI.Services;

public interface ISecurityService
{
    bool HasAdministrativeRight(ClaimsPrincipal user, AdministrativeRights right);
    AdministrativeRights GetUserAdministrativeRights(ClaimsPrincipal user);
    public ApplicationRights GetUserApplicationRights(ClaimsPrincipal user);
    public bool HasApplicationRight(ClaimsPrincipal user, ApplicationRights right);
    string HashPassword(UserAccount userAccount, string password);
    bool VerifyPassword(UserAccount userAccount, string password);
    string? GetUserId(ClaimsPrincipal user);

    /// <summary>
    /// Prüft, ob der aufrufende Nutzer über ein Konto mit den Rechten <paramref name="targetRights"/>
    /// verfügen darf – also ob er dieses Konto bearbeiten, aktivieren oder löschen darf.
    /// </summary>
    /// <param name="user">Der aufrufende Nutzer.</param>
    /// <param name="targetRights">Die administrativen Rechte des betroffenen Kontos.</param>
    /// <returns><c>true</c>, wenn der Aufrufer jedes Recht des Zielkontos selbst besitzt.</returns>
    bool CanActOnAdministrativeRights(ClaimsPrincipal user, AdministrativeRights targetRights);

    /// <summary>
    /// Prüft, ob der Aufrufer jedes einzelne der <paramref name="requested"/> Anwendungsrechte
    /// selbst besitzt und es daher weitergeben darf.
    /// </summary>
    /// <param name="user">Der aufrufende Nutzer.</param>
    /// <param name="requested">Die zu vergebenden Anwendungsrechte.</param>
    bool CanGrantApplicationRights(ClaimsPrincipal user, ApplicationRights requested);
}

public class SecurityService : ISecurityService
{
    /// <summary>
    /// Prüft, ob <paramref name="held"/> eine Obermenge von <paramref name="required"/> ist.
    /// </summary>
    /// <param name="held">Die Rechte, die der Aufrufer besitzt.</param>
    /// <param name="required">Die Rechte, die vorhanden sein müssen.</param>
    /// <returns><c>true</c>, wenn kein erforderliches Bit fehlt.</returns>
    /// <remarks>
    /// <para>
    /// Diese Methode ersetzt jeden Vergleich der Form <c>callerRights &lt; targetRights</c>.
    /// Ein solcher Vergleich ist bei <see cref="FlagsAttribute"/>-Enums sachlich falsch, weil
    /// Bitfelder keine Ordnung besitzen: Ein Nutzer mit ausschließlich
    /// <see cref="AdministrativeRights.DeleteUsers"/> (Wert 4) galt gegenüber einem Konto mit
    /// <c>CreateUsers | EditUsers</c> (Wert 3) als höherwertig, obwohl er keines dieser beiden
    /// Rechte besitzt. Der Zahlenvergleich erteilte damit eine Berechtigung, die fachlich nicht
    /// vorlag.
    /// </para>
    /// <para>
    /// Die korrekte Prüfung ist eine Teilmengenprüfung: Es darf kein Bit gesetzt sein, das der
    /// Aufrufer nicht selbst hält. <see cref="AdministrativeRights.Administrate"/> ist
    /// <c>int.MaxValue</c> und erfüllt damit jede Anforderung – das gewünschte Verhalten für
    /// einen Administrator.
    /// </para>
    /// </remarks>
    private static bool Covers(int held, int required) => (required & ~held) == 0;

    public bool CanActOnAdministrativeRights(ClaimsPrincipal user, AdministrativeRights targetRights)
    {
        return Covers((int)GetUserAdministrativeRights(user), (int)targetRights);
    }

    public bool CanGrantApplicationRights(ClaimsPrincipal user, ApplicationRights requested)
    {
        return Covers((int)GetUserApplicationRights(user), (int)requested);
    }

    public bool HasAdministrativeRight(ClaimsPrincipal user, AdministrativeRights right)
    {
        var administrativeRightsClaim = user.Claims.FirstOrDefault(c => c.Type == "AdministrativeRights")?.Value;
        return Enum.TryParse(administrativeRightsClaim, out AdministrativeRights rights) && rights.HasFlag(right);
    }

    public AdministrativeRights GetUserAdministrativeRights(ClaimsPrincipal user)
    {
        var administrativeRightsClaim = user.Claims.FirstOrDefault(c => c.Type == "AdministrativeRights")?.Value;
        return Enum.TryParse(administrativeRightsClaim, out AdministrativeRights rights) ? rights : 0;
    }

    public ApplicationRights GetUserApplicationRights(ClaimsPrincipal user)
    {
        var applicationRightsClaim = user.Claims.FirstOrDefault(c => c.Type == "ApplicationRights")?.Value;
        return Enum.TryParse(applicationRightsClaim, out ApplicationRights rights) ? rights : 0;
    }

    public bool HasApplicationRight(ClaimsPrincipal user, ApplicationRights right)
    {
        var applicationRightsClaim = user.Claims.FirstOrDefault(c => c.Type == "ApplicationRights")?.Value;
        return Enum.TryParse(applicationRightsClaim, out ApplicationRights rights) && rights.HasFlag(right);
    }

    public string HashPassword(UserAccount userAccount, string password)
    {
        return new PasswordHasher<UserAccount>().HashPassword(userAccount, password);
    }

    public bool VerifyPassword(UserAccount userAccount, string password)
    {
        var passwordVerificationResult = new PasswordHasher<UserAccount>().VerifyHashedPassword(userAccount, userAccount.PasswordHash, password);
        return passwordVerificationResult == PasswordVerificationResult.Success;
    }

    public string? GetUserId(ClaimsPrincipal user)
    {
        return user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
    }
}