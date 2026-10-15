using Microsoft.AspNetCore.Mvc;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using SmartRoutingAPI.Contracts;
using SmartRoutingAPI.Services;

namespace SmartRoutingAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class AccountsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly RoutingDBContext _routingDBContext;
    private readonly ISecurityService _securityService;

    public AccountsController(IConfiguration config, RoutingDBContext dBContext, ISecurityService securityService)
    {
        _config = config;
        _routingDBContext = dBContext;
        _securityService = securityService;
    }

    /// <summary>Erstellt einen neuen Nutzer-Account.</summary>
    /// <response code="201">Der neu erstellte Account.</response>
    /// <response code="400">Ungültige Anfrage.</response>
    /// <response code="403">Fehlende Rechte.</response>
    /// <response code="409">Benutzername existiert bereits.</response>
    [HttpPost]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    [CheckAdministrativePermission(AdministrativeRights.CreateUsers)]
    public async Task<IActionResult> CreateAccount([FromBody] CreateAccountRequest createAccount, CancellationToken ct)
    {
        if (createAccount == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (await _routingDBContext.UserAccounts.AnyAsync(u => u.Username == createAccount.Username, ct))
        {
            return Conflict("A user with the same username already exists.");
        }

        // Niemand darf mehr Rechte vergeben, als er selbst besitzt – weder administrativ noch
        // anwendungsseitig. Beide Prüfungen sind Teilmengenprüfungen auf Bitfeldern; ein
        // Zahlenvergleich wäre hier falsch (siehe SecurityService.Covers).
        if (!_securityService.CanActOnAdministrativeRights(User, createAccount.AdministrativeRights)
            || !_securityService.CanGrantApplicationRights(User, createAccount.ApplicationRights))
        {
            return Forbid();
        }

        var userAccount = new UserAccount
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Email = createAccount.Email,
            FirstName = createAccount.FirstName,
            LastName = createAccount.LastName,
            LastLogin = DateTimeOffset.UtcNow,
            ApplicationRights = createAccount.ApplicationRights,
            AdministrativeRights = createAccount.AdministrativeRights,
            IsActive = true,
            Username = createAccount.Username
        };

        userAccount.PasswordHash = _securityService.HashPassword(userAccount, createAccount.Password);

        await _routingDBContext.UserAccounts.AddAsync(userAccount, ct);
        await _routingDBContext.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetUserById), new { id = userAccount.Id }, userAccount.ToResponse());
    }

    /// <summary>Bearbeitet einen bestehenden Nutzer-Account.</summary>
    /// <response code="200">Der aktualisierte Account.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> EditAccount(int id, [FromBody] EditAccountRequest editAccount, CancellationToken ct)
    {
        if (editAccount == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        // Zwei getrennte Prüfungen sind erforderlich: der Aufrufer muss das Konto in seinem
        // bisherigen Zustand bearbeiten dürfen (sonst könnte er ein höherwertiges Konto
        // herabsetzen) und darf ihm zugleich keine Rechte verleihen, die er selbst nicht hat
        // (sonst wäre die Rechteerweiterung über einen Zweitaccount möglich).
        if (!_securityService.CanActOnAdministrativeRights(User, userAccount.AdministrativeRights)
            || !_securityService.CanActOnAdministrativeRights(User, editAccount.AdministrativeRights)
            || !_securityService.CanGrantApplicationRights(User, editAccount.ApplicationRights))
        {
            return Forbid();
        }

        userAccount.Username = editAccount.Username ?? userAccount.Username;
        userAccount.Email = editAccount.Email ?? userAccount.Email;
        userAccount.FirstName = editAccount.FirstName ?? userAccount.FirstName;
        userAccount.LastName = editAccount.LastName ?? userAccount.LastName;
        userAccount.IsActive = editAccount.IsActive;
        userAccount.ApplicationRights = editAccount.ApplicationRights;
        userAccount.AdministrativeRights = editAccount.AdministrativeRights;

        await _routingDBContext.SaveChangesAsync(ct);

        return Ok(userAccount.ToResponse());
    }

    /// <summary>Löscht den Nutzer-Account mit der angegebenen ID.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.DeleteUsers)]
    public async Task<IActionResult> DeleteAccount(int id, CancellationToken ct)
    {
        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        if (!_securityService.CanActOnAdministrativeRights(User, userAccount.AdministrativeRights))
        {
            return Forbid();
        }

        if (_securityService.GetUserId(User) == id.ToString())
        {
            return BadRequest("You cannot delete your own account.");
        }

        _routingDBContext.UserAccounts.Remove(userAccount);
        await _routingDBContext.SaveChangesAsync(ct);

        return Ok();
    }

    /// <summary>Löscht den aktuellen Nutzer-Account (Passwort-Bestätigung erforderlich).</summary>
    [HttpDelete("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteOwnAccount([FromQuery] string currentPassword, CancellationToken ct)
    {
        var userId = _securityService.GetUserId(User);
        if (userId == null || !int.TryParse(userId, out int userIdInt))
        {
            return Unauthorized();
        }

        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { userIdInt }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        if (!_securityService.VerifyPassword(userAccount, currentPassword))
        {
            return BadRequest("User-ID or password is incorrect.");
        }

        _routingDBContext.UserAccounts.Remove(userAccount);
        await _routingDBContext.SaveChangesAsync(ct);

        return Ok();
    }

    /// <summary>Ändert das Passwort des Nutzer-Accounts mit der angegebenen ID.</summary>
    [HttpPost("{id:int}/changePassword")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword(int id, [FromBody] ChangePasswordRequest changePasswordRequest, CancellationToken ct)
    {
        if (changePasswordRequest == null || !ModelState.IsValid)
        {
            return BadRequest();
        }

        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        var userId = _securityService.GetUserId(User);
        if (userId == null)
        {
            return Unauthorized();
        }

        if (userId == id.ToString())
        {
            if (!_securityService.VerifyPassword(userAccount, changePasswordRequest.CurrentPassword))
            {
                return BadRequest("Current password is incorrect.");
            }
        }
        else if (!_securityService.HasAdministrativeRight(User, AdministrativeRights.Administrate))
        {
            return Forbid();
        }

        userAccount.PasswordHash = _securityService.HashPassword(userAccount, changePasswordRequest.NewPassword);
        await _routingDBContext.SaveChangesAsync(ct);

        return Ok(userId == id.ToString() ? "Own password changed successfully." : "Password changed successfully.");
    }

    /// <summary>Zeigt das Profil des aktuellen Benutzers an.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyProfile(CancellationToken ct)
    {
        var userId = _securityService.GetUserId(User);
        if (userId == null || !int.TryParse(userId, out int userIdInt))
        {
            return Unauthorized();
        }

        var userAccount = await _routingDBContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userIdInt, ct);
        return userAccount == null ? NotFound() : Ok(userAccount.ToResponse());
    }

    /// <summary>
    /// Bearbeitet die eigenen Stammdaten (Name und E-Mail-Adresse).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dieser Endpunkt ändert <b>ausschließlich</b> Stammdaten. Rechte und der Aktivierungsstatus
    /// sind bewusst nicht enthalten: Es handelt sich um den einzigen Schreibzugriff, den ein
    /// beliebiger angemeldeter Nutzer auf sein eigenes Konto hat, und ein Konto darf sich niemals
    /// selbst erweitern.
    /// </para>
    /// <para>
    /// Die Vorversion nahm hier ein vollständiges <c>EditAccountRequest</c> an und übernahm daraus
    /// <c>AdministrativeRights</c>. Die vorgeschaltete Prüfung verglich die Rechte des Aufrufers mit
    /// den Rechten <em>des eigenen Kontos</em> – also einen Wert mit sich selbst – und konnte damit
    /// nie greifen. Jeder angemeldete Nutzer konnte sich auf diesem Weg
    /// <see cref="AdministrativeRights.Administrate"/> zuweisen. Rechteänderungen laufen daher
    /// jetzt ausnahmslos über die Endpunkte unter <c>/api/Accounts/{id}/rights/…</c>, die
    /// <see cref="AdministrativeRights.EditUsers"/> erfordern.
    /// </para>
    /// </remarks>
    /// <response code="200">Das aktualisierte Profil.</response>
    /// <response code="400">Ungültige Eingabedaten.</response>
    /// <response code="401">Kein gültiges Token.</response>
    /// <response code="404">Das eigene Konto existiert nicht mehr.</response>
    [HttpPut("me")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EditMyProfile([FromBody] EditOwnProfileRequest editAccount, CancellationToken ct)
    {
        if (editAccount == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = _securityService.GetUserId(User);
        if (userId == null || !int.TryParse(userId, out int userIdInt))
        {
            return Unauthorized();
        }

        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { userIdInt }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        userAccount.Email = editAccount.Email ?? userAccount.Email;
        userAccount.FirstName = editAccount.FirstName ?? userAccount.FirstName;
        userAccount.LastName = editAccount.LastName ?? userAccount.LastName;

        await _routingDBContext.SaveChangesAsync(ct);

        return Ok(userAccount.ToResponse());
    }

    /// <summary>Zeigt eine (paginierte) Liste aller Benutzerkonten an.</summary>
    [HttpGet]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<UserAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> GetAllUsers([FromQuery] PageQuery page, CancellationToken ct)
    {
        var baseQuery = _routingDBContext.UserAccounts.AsNoTracking().OrderBy(u => u.Id);
        var total = await baseQuery.CountAsync(ct);

        var users = await baseQuery.Skip(page.Skip).Take(page.Take).ToListAsync(ct);
        var items = users.Select(u => u.ToResponse()).ToList();

        return Ok(new PagedResult<UserAccountResponse>(items, page.Page, page.PageSize, total));
    }

    /// <summary>Zeigt die Details eines bestimmten Benutzers an.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> GetUserById(int id, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user == null ? NotFound() : Ok(user.ToResponse());
    }

    /// <summary>Aktiviert einen Benutzer-Account.</summary>
    [HttpPost("{id:int}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> ActivateUser(int id, CancellationToken ct)
    {
        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        if (!_securityService.CanActOnAdministrativeRights(User, userAccount.AdministrativeRights))
        {
            return Forbid();
        }

        userAccount.IsActive = true;
        await _routingDBContext.SaveChangesAsync(ct);

        return Ok();
    }

    /// <summary>Deaktiviert einen Benutzer-Account.</summary>
    [HttpPost("{id:int}/deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> DeactivateUser(int id, CancellationToken ct)
    {
        var userAccount = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (userAccount == null)
        {
            return NotFound();
        }

        if (!_securityService.CanActOnAdministrativeRights(User, userAccount.AdministrativeRights))
        {
            return Forbid();
        }

        userAccount.IsActive = false;
        await _routingDBContext.SaveChangesAsync(ct);

        return Ok();
    }

    // ==================== PASSWORD RESET ====================
    //
    // Selbstbedienungs-Zurücksetzung ist NICHT implementiert und darf nicht vorgetäuscht werden.
    //
    // Die Vorversion bot unter POST /api/Accounts/resetPasswordConfirm einen anonymen Endpunkt,
    // der zu einer beliebigen E-Mail-Adresse ein neues Passwort setzte – ohne Token, ohne Code,
    // ohne jede Prüfung. Wer eine E-Mail-Adresse kannte, übernahm das Konto vollständig. Die
    // zugehörige Anforderungsseite (resetPasswordRequest) lieferte dagegen 501: Es existierte also
    // ausschließlich die Bestätigungshälfte eines nie gebauten Verfahrens.
    //
    // Beide Endpunkte sind entfernt. Bis ein Verfahren mit signiertem, kurzlebigem Token und
    // E-Mail-Versand vorliegt, wird ein Passwort ausschließlich über
    // POST /api/Accounts/{id}/changePassword gesetzt – dieser Endpunkt verlangt entweder das
    // aktuelle Passwort (eigenes Konto) oder AdministrativeRights.Administrate (fremdes Konto).
    //
    // Anforderungen an eine künftige Umsetzung:
    //   * Token: zufällig (>=128 Bit), gehasht gespeichert, Lebensdauer <= 30 Minuten, einmalig.
    //   * Antwort immer identisch, unabhängig davon, ob das Konto existiert (keine Kontoaufzählung).
    //   * Rate-Limit auf der Anforderungsseite.

    /// <summary>Zeigt das Aktivitätsprotokoll eines Benutzers an.</summary>
    /// <remarks>
    /// Noch nicht umgesetzt: Es existiert keine Protokolltabelle. Der Endpunkt liefert bewusst
    /// <c>501</c> statt einer leeren Liste, weil eine leere Liste "keine Aktivität" bedeutet und
    /// damit eine Aussage träfe, die das System nicht belegen kann.
    /// </remarks>
    /// <response code="501">Nicht implementiert.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpGet("{id:int}/activity")]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUserActivity)]
    public async Task<IActionResult> GetUserActivity(int id, CancellationToken ct)
    {
        var exists = await _routingDBContext.UserAccounts.AnyAsync(u => u.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        return StatusCode(StatusCodes.Status501NotImplemented, "User activity logging is not implemented.");
    }

    /// <summary>Überprüft die Rechte eines Benutzers.</summary>
    [HttpGet("{id:int}/permissions")]
    [ProducesResponseType(typeof(UserPermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> GetUserPermissions(int id, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user == null)
        {
            return NotFound();
        }

        return Ok(new UserPermissionsResponse(
            user.AdministrativeRights.ToString(),
            user.ApplicationRights.ToString()));
    }

    // ==================== BULK OPERATIONS ====================

    /// <summary>Erstellt mehrere Nutzer-Accounts gleichzeitig.</summary>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(BulkCreateResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.CreateUsers)]
    public async Task<IActionResult> BulkCreateAccounts([FromBody] List<CreateAccountRequest> createAccounts, CancellationToken ct)
    {
        if (createAccounts == null || createAccounts.Count == 0)
        {
            return BadRequest("No accounts provided");
        }

        var createdAccounts = new List<UserAccount>();
        var errors = new List<string>();
        var currentUserRights = _securityService.GetUserAdministrativeRights(User);

        foreach (var createAccount in createAccounts)
        {
            if (await _routingDBContext.UserAccounts.AnyAsync(u => u.Username == createAccount.Username, ct))
            {
                errors.Add($"User '{createAccount.Username}' already exists");
                continue;
            }

            if (!_securityService.CanActOnAdministrativeRights(User, createAccount.AdministrativeRights)
                || !_securityService.CanGrantApplicationRights(User, createAccount.ApplicationRights))
            {
                errors.Add(
                    $"User '{createAccount.Username}' would receive rights the caller does not hold");
                continue;
            }

            var userAccount = new UserAccount
            {
                CreatedAt = DateTimeOffset.UtcNow,
                Email = createAccount.Email,
                FirstName = createAccount.FirstName,
                LastName = createAccount.LastName,
                LastLogin = DateTimeOffset.UtcNow,
                ApplicationRights = createAccount.ApplicationRights,
                AdministrativeRights = createAccount.AdministrativeRights,
                IsActive = true,
                Username = createAccount.Username
            };

            userAccount.PasswordHash = _securityService.HashPassword(userAccount, createAccount.Password);
            await _routingDBContext.UserAccounts.AddAsync(userAccount, ct);
            createdAccounts.Add(userAccount);
        }

        await _routingDBContext.SaveChangesAsync(ct);

        var result = new BulkCreateResultResponse(
            createdAccounts.Select(u => u.ToResponse()).ToList(),
            errors);
        return Ok(result);
    }

    /// <summary>Aktiviert mehrere Benutzer-Accounts gleichzeitig.</summary>
    [HttpPost("bulk/activate")]
    [ProducesResponseType(typeof(BulkOperationResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> BulkActivateUsers([FromBody] List<int> userIds, CancellationToken ct)
        => await BulkSetActiveAsync(userIds, true, ct);

    /// <summary>Deaktiviert mehrere Benutzer-Accounts gleichzeitig.</summary>
    [HttpPost("bulk/deactivate")]
    [ProducesResponseType(typeof(BulkOperationResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> BulkDeactivateUsers([FromBody] List<int> userIds, CancellationToken ct)
        => await BulkSetActiveAsync(userIds, false, ct);

    private async Task<IActionResult> BulkSetActiveAsync(List<int> userIds, bool active, CancellationToken ct)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return BadRequest("No user IDs provided");
        }

        var currentUserRights = _securityService.GetUserAdministrativeRights(User);
        var users = await _routingDBContext.UserAccounts.Where(u => userIds.Contains(u.Id)).ToListAsync(ct);

        int successCount = 0;
        var errors = new List<string>();

        foreach (var user in users)
        {
            if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights))
            {
                errors.Add($"User {user.Id} holds administrative rights the caller does not have");
                continue;
            }

            user.IsActive = active;
            successCount++;
        }

        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(new BulkOperationResultResponse(successCount, errors));
    }

    /// <summary>Löscht mehrere Benutzer-Accounts gleichzeitig.</summary>
    [HttpDelete("bulk")]
    [ProducesResponseType(typeof(BulkOperationResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.DeleteUsers)]
    public async Task<IActionResult> BulkDeleteUsers([FromBody] List<int> userIds, CancellationToken ct)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return BadRequest("No user IDs provided");
        }

        var currentUserId = _securityService.GetUserId(User);
        var currentUserRights = _securityService.GetUserAdministrativeRights(User);
        var users = await _routingDBContext.UserAccounts.Where(u => userIds.Contains(u.Id)).ToListAsync(ct);

        int successCount = 0;
        var errors = new List<string>();

        foreach (var user in users)
        {
            if (user.Id.ToString() == currentUserId)
            {
                errors.Add($"Cannot delete your own account (ID: {user.Id})");
                continue;
            }

            if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights))
            {
                errors.Add($"User {user.Id} holds administrative rights the caller does not have");
                continue;
            }

            _routingDBContext.UserAccounts.Remove(user);
            successCount++;
        }

        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(new BulkOperationResultResponse(successCount, errors));
    }

    // ==================== SEARCH & FILTERING ====================

    /// <summary>Sucht Benutzer nach verschiedenen Kriterien (paginiert).</summary>
    [HttpGet("search")]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<UserAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> SearchUsers(
        [FromQuery] PageQuery page,
        [FromQuery] string? searchTerm = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] AdministrativeRights? administrativeRights = null,
        [FromQuery] ApplicationRights? applicationRights = null,
        CancellationToken ct = default)
    {
        var query = _routingDBContext.UserAccounts.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.ToLower();
            query = query.Where(u =>
                u.Username.ToLower().Contains(term) ||
                u.Email.ToLower().Contains(term) ||
                u.FirstName.ToLower().Contains(term) ||
                u.LastName.ToLower().Contains(term));
        }

        if (isActive.HasValue)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        if (administrativeRights.HasValue)
        {
            query = query.Where(u => u.AdministrativeRights.HasFlag(administrativeRights.Value));
        }

        if (applicationRights.HasValue)
        {
            query = query.Where(u => u.ApplicationRights.HasFlag(applicationRights.Value));
        }

        query = query.OrderBy(u => u.Id);
        var total = await query.CountAsync(ct);
        var users = await query.Skip(page.Skip).Take(page.Take).ToListAsync(ct);

        return Ok(new PagedResult<UserAccountResponse>(
            users.Select(u => u.ToResponse()).ToList(), page.Page, page.PageSize, total));
    }

    /// <summary>Filtert Benutzer nach administrativen Rechten (paginiert).</summary>
    [HttpGet("filter/administrative/{right}")]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<UserAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> FilterByAdministrativeRight(AdministrativeRights right, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var query = _routingDBContext.UserAccounts.AsNoTracking()
            .Where(u => u.AdministrativeRights.HasFlag(right))
            .OrderBy(u => u.Id);

        var total = await query.CountAsync(ct);
        var users = await query.Skip(page.Skip).Take(page.Take).ToListAsync(ct);

        return Ok(new PagedResult<UserAccountResponse>(
            users.Select(u => u.ToResponse()).ToList(), page.Page, page.PageSize, total));
    }

    /// <summary>Filtert Benutzer nach Anwendungsrechten (paginiert).</summary>
    [HttpGet("filter/application/{right}")]
    [ForgivingRateLimit]
    [ProducesResponseType(typeof(PagedResult<UserAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> FilterByApplicationRight(ApplicationRights right, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var query = _routingDBContext.UserAccounts.AsNoTracking()
            .Where(u => u.ApplicationRights.HasFlag(right))
            .OrderBy(u => u.Id);

        var total = await query.CountAsync(ct);
        var users = await query.Skip(page.Skip).Take(page.Take).ToListAsync(ct);

        return Ok(new PagedResult<UserAccountResponse>(
            users.Select(u => u.ToResponse()).ToList(), page.Page, page.PageSize, total));
    }

    // ==================== USER STATISTICS ====================

    /// <summary>Gibt Statistiken über Benutzer zurück.</summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(UserStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [CheckAdministrativePermission(AdministrativeRights.ViewUsers)]
    public async Task<IActionResult> GetUserStatistics(CancellationToken ct)
    {
        var totalUsers = await _routingDBContext.UserAccounts.CountAsync(ct);
        var activeUsers = await _routingDBContext.UserAccounts.CountAsync(u => u.IsActive, ct);
        var recentlyCreated = await _routingDBContext.UserAccounts
            .CountAsync(u => u.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30), ct);

        // Load the rights columns ONCE (previously the whole table was loaded twice, §4.5 / P5).
        var rights = await _routingDBContext.UserAccounts.AsNoTracking()
            .Select(u => new { u.AdministrativeRights, u.ApplicationRights })
            .ToListAsync(ct);

        var adminCounts = new Dictionary<string, int>();
        foreach (AdministrativeRights right in Enum.GetValues<AdministrativeRights>())
        {
            if (right == AdministrativeRights.None) continue;
            var count = rights.Count(r => r.AdministrativeRights.HasFlag(right));
            if (count > 0) adminCounts[right.ToString()] = count;
        }

        var appCounts = new Dictionary<string, int>();
        foreach (ApplicationRights right in Enum.GetValues<ApplicationRights>())
        {
            if (right == ApplicationRights.Unknown) continue;
            var count = rights.Count(r => r.ApplicationRights.HasFlag(right));
            if (count > 0) appCounts[right.ToString()] = count;
        }

        var statistics = new UserStatisticsResponse(
            totalUsers, activeUsers, totalUsers - activeUsers, recentlyCreated, adminCounts, appCounts);

        return Ok(statistics);
    }

    // ==================== GRANULAR RIGHTS MANAGEMENT ====================
    //
    // Zwei Bedingungen müssen für jede Rechteänderung erfüllt sein:
    //
    //   1. Der Aufrufer muss das Zielkonto in seinem bisherigen Zustand bearbeiten dürfen –
    //      andernfalls könnte er einem höherwertigen Konto Rechte entziehen.
    //   2. Der Aufrufer muss das betroffene Recht selbst besitzen – andernfalls könnte er über
    //      ein zweites Konto Rechte erlangen, die er nie hatte ("Rechte-Wäsche").
    //
    // Bedingung 2 fehlte bisher vollständig. Zusammen mit dem Bitfeld-Zahlenvergleich genügte
    // EditUsers, um beliebigen Konten Administrate zu verleihen.

    /// <summary>Gewährt einem Benutzer ein administratives Recht.</summary>
    /// <response code="200">Das aktualisierte Konto.</response>
    /// <response code="403">Der Aufrufer besitzt das Recht selbst nicht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPost("{id:int}/rights/administrative/grant")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> GrantAdministrativeRight(int id, [FromQuery] AdministrativeRights right, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (user == null) return NotFound();

        if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights)
            || !_securityService.CanActOnAdministrativeRights(User, right))
        {
            return Forbid();
        }

        user.AdministrativeRights |= right;
        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(user.ToResponse());
    }

    /// <summary>Entzieht einem Benutzer ein administratives Recht.</summary>
    /// <response code="200">Das aktualisierte Konto.</response>
    /// <response code="403">Der Aufrufer darf dieses Konto nicht bearbeiten.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPost("{id:int}/rights/administrative/revoke")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> RevokeAdministrativeRight(int id, [FromQuery] AdministrativeRights right, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (user == null) return NotFound();

        if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights))
        {
            return Forbid();
        }

        user.AdministrativeRights &= ~right;
        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(user.ToResponse());
    }

    /// <summary>Gewährt einem Benutzer ein Anwendungsrecht.</summary>
    /// <response code="200">Das aktualisierte Konto.</response>
    /// <response code="403">Der Aufrufer besitzt das Recht selbst nicht.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPost("{id:int}/rights/application/grant")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> GrantApplicationRight(int id, [FromQuery] ApplicationRights right, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (user == null) return NotFound();

        if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights)
            || !_securityService.CanGrantApplicationRights(User, right))
        {
            return Forbid();
        }

        user.ApplicationRights |= right;
        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(user.ToResponse());
    }

    /// <summary>Entzieht einem Benutzer ein Anwendungsrecht.</summary>
    /// <response code="200">Das aktualisierte Konto.</response>
    /// <response code="403">Der Aufrufer darf dieses Konto nicht bearbeiten.</response>
    /// <response code="404">Nicht gefunden.</response>
    [HttpPost("{id:int}/rights/application/revoke")]
    [ProducesResponseType(typeof(UserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [CheckAdministrativePermission(AdministrativeRights.EditUsers)]
    public async Task<IActionResult> RevokeApplicationRight(int id, [FromQuery] ApplicationRights right, CancellationToken ct)
    {
        var user = await _routingDBContext.UserAccounts.FindAsync(new object[] { id }, ct);
        if (user == null) return NotFound();

        if (!_securityService.CanActOnAdministrativeRights(User, user.AdministrativeRights))
        {
            return Forbid();
        }

        user.ApplicationRights &= ~right;
        await _routingDBContext.SaveChangesAsync(ct);
        return Ok(user.ToResponse());
    }
}

// ==================== Request DTOs ====================

public class CreateAccountRequest
{
    [Required]
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    [Required]
    public AdministrativeRights AdministrativeRights { get; set; }

    [Required]
    public ApplicationRights ApplicationRights { get; set; }
}

/// <summary>Payload für die administrative Bearbeitung eines fremden Kontos.</summary>
public class EditAccountRequest
{
    [Required]
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public ApplicationRights ApplicationRights { get; set; }
    public AdministrativeRights AdministrativeRights { get; set; }
}

/// <summary>
/// Payload für <c>PUT /api/Accounts/me</c>. Enthält ausschließlich Stammdaten.
/// </summary>
/// <remarks>
/// Bewusst ein eigener Typ und nicht <see cref="EditAccountRequest"/>: Ein gemeinsamer Typ hatte
/// zur Folge, dass der Selbstbedienungs-Endpunkt Rechtefelder annahm und übernahm. Die Trennung
/// macht die Rechteerweiterung strukturell unmöglich statt sie nur zu prüfen. Auch
/// <c>Username</c> und <c>IsActive</c> fehlen absichtlich: Der Anmeldename ist der Schlüssel der
/// Authentifizierung, und ein Konto darf sich nicht selbst deaktivieren.
/// </remarks>
public class EditOwnProfileRequest
{
    [EmailAddress, MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? FirstName { get; set; }

    [MaxLength(50)]
    public string? LastName { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required]
    public string NewPassword { get; set; } = "";
}
