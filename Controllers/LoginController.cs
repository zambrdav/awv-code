using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Accounts;

namespace SmartRoutingAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
[Produces("application/json")]
public class LoginController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly RoutingDBContext _routingDBContext;

    public LoginController(IConfiguration config, RoutingDBContext dBContext)
    {
        _config = config;
        _routingDBContext = dBContext;
    }

    /// <summary>Authentifiziert einen Benutzer und gibt ein JWT zurück.</summary>
    /// <remarks>
    /// Der JWT ist für die Verwendung der restlichen API-Endpunkte erforderlich. Unerwartete Fehler
    /// werden vom globalen Fehler-Handler als <c>500 ProblemDetails</c> behandelt (keine Stacktraces mehr).
    /// </remarks>
    /// <response code="200">JWT bei erfolgreicher Authentifizierung.</response>
    /// <response code="400">Ungültige oder unvollständige Eingabedaten.</response>
    /// <response code="500">Interner Serverfehler (z. B. fehlende JWT-Konfiguration).</response>
    [HttpPost]
    [ProducesResponseType(typeof(TokenResponse), 200)]
    [ProducesResponseType(typeof(string), 400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> Post([FromBody] LoginRequest loginRequest, CancellationToken ct)
    {
        if (loginRequest == null || !ModelState.IsValid)
        {
            return BadRequest("Login-Anfrage ist ungültig.");
        }

        UserAccount? authenticatedAccount;

        if (!await _routingDBContext.UserAccounts.AnyAsync(ct))
        {
            authenticatedAccount = await CreateBootstrapAccountAsync(loginRequest, ct);
        }
        else
        {
            authenticatedAccount = await AuthenticateExistingAccountAsync(loginRequest, ct);
        }

        if (authenticatedAccount == null)
        {
            return BadRequest("Anmeldedaten ungültig.");
        }

        var jwtKey = _config["Jwt:Key"];
        if (string.IsNullOrEmpty(jwtKey))
        {
            return StatusCode(500, "Interner Serverfehler: JWT-Schlüssel nicht konfiguriert.");
        }

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new JwtSecurityToken(
            _config["Jwt:Issuer"],
            _config["Jwt:Issuer"],
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, authenticatedAccount.Id.ToString()),
                new Claim(ClaimTypes.Name, authenticatedAccount.Username),
                new Claim("ApplicationRights", authenticatedAccount.ApplicationRights.ToString()),
                new Claim("AdministrativeRights", authenticatedAccount.AdministrativeRights.ToString())
            },
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials);

        var token = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
        return Ok(new TokenResponse(token));
    }

    /// <summary>
    /// Legt beim allerersten Anmelden das Administratorkonto aus <c>DefaultAccount</c> an
    /// und <b>speichert es</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Der entscheidende Punkt ist das Speichern. Die Vorversion erzeugte das Konto nur im
    /// Arbeitsspeicher und schrieb es nie in die Datenbank. Daraus folgten drei Fehler, die
    /// zusammen die Anwendung unbenutzbar machten:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>Id</c> blieb <c>0</c>, weil sie nur von der Datenbank vergeben wird. Das Token trug
    ///     damit <c>NameIdentifier = "0"</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>GET /api/Accounts/me</c> suchte nach <c>Id == 0</c>, fand nichts und antwortete mit
    ///     <c>404</c>. Das Frontend konnte die Rechte des angemeldeten Nutzers nicht ermitteln und
    ///     zeigte folgerichtig „Kein Zugriff“ – obwohl das Token volle Rechte trug.
    ///   </description></item>
    ///   <item><description>
    ///     <c>UserAccounts.Any()</c> blieb dauerhaft <c>false</c>. Die Übergabe von den
    ///     Konfigurationswerten an ein echtes Konto fand deshalb nie statt.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Zusätzlich wurde der Anmeldename fest auf „Administrator“ gesetzt, während gegen
    /// <c>DefaultAccount:Username</c> geprüft wurde, und es wurde kein Passwort-Hash gebildet. Ein
    /// so gespeichertes Konto hätte sich nie wieder anmelden können. Beides ist hier behoben: Der
    /// konfigurierte Anmeldename wird übernommen und das konfigurierte Passwort wird gehasht
    /// hinterlegt.
    /// </para>
    /// <para>
    /// Nach diesem Aufruf existiert ein vollwertiges Administratorkonto in der Datenbank. Der
    /// Abschnitt <c>DefaultAccount</c> ist damit wirkungslos und kann entfernt werden.
    /// </para>
    /// </remarks>
    /// <param name="loginRequest">Die Anmeldedaten.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Das angelegte Konto, oder <c>null</c>, wenn die Angaben nicht passen.</returns>
    private async Task<UserAccount?> CreateBootstrapAccountAsync(LoginRequest loginRequest, CancellationToken ct)
    {
        try
        {
            var username = _config["DefaultAccount:Username"];
            var password = _config["DefaultAccount:Password"];

            if (string.IsNullOrEmpty(username)
                || string.IsNullOrEmpty(password)
                || loginRequest.Username != username
                || loginRequest.Password != password)
            {
                return null;
            }

            var account = new UserAccount
            {
                Username = username,
                Email = _config["DefaultAccount:Email"] ?? "admin@localhost",
                FirstName = "Administrator",
                LastName = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLogin = DateTimeOffset.UtcNow,
                ApplicationRights = ApplicationRights.All,
                AdministrativeRights = AdministrativeRights.Administrate,
                IsActive = true
            };

            // Der Hash muss nach dem Setzen der übrigen Felder gebildet werden: PasswordHasher
            // erhält die Entität als Kontext.
            account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, password);

            _routingDBContext.UserAccounts.Add(account);
            await _routingDBContext.SaveChangesAsync(ct);

            return account;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Das Administratorkonto aus DefaultAccount konnte nicht angelegt werden.", ex);
        }
    }

    /// <summary>Prüft die Anmeldedaten gegen ein bestehendes Konto.</summary>
    /// <param name="loginRequest">Die Anmeldedaten.</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Das Konto, oder <c>null</c>, wenn Name, Passwort oder Status nicht passen.</returns>
    private async Task<UserAccount?> AuthenticateExistingAccountAsync(LoginRequest loginRequest, CancellationToken ct)
    {
        try
        {
            var account = await _routingDBContext.UserAccounts
                .FirstOrDefaultAsync(u => u.Username == loginRequest.Username, ct);

            if (account == null)
            {
                return null;
            }

            var hasher = new PasswordHasher<UserAccount>();
            var result = hasher.VerifyHashedPassword(account, account.PasswordHash, loginRequest.Password);

            if (result is not (PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded))
            {
                return null;
            }

            // Ein deaktiviertes Konto darf sich nicht anmelden. Diese Prüfung fehlte, wodurch die
            // Endpunkte /activate und /deactivate ohne jede Wirkung waren: Ein deaktivierter Nutzer
            // erhielt weiterhin ein gültiges Token mit unveränderten Rechten.
            if (!account.IsActive)
            {
                return null;
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                account.PasswordHash = hasher.HashPassword(account, loginRequest.Password);
            }

            account.LastLogin = DateTimeOffset.UtcNow;
            await _routingDBContext.SaveChangesAsync(ct);

            return account;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die Anmeldung konnte nicht geprüft werden.", ex);
        }
    }
}

/// <summary>Antwort des Login-Endpunkts.</summary>
public sealed record TokenResponse(string Token);

public class LoginRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Username { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Required]
    public string Password { get; set; } = "";
}
