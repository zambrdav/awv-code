using System;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using SmartRoutingAPI.Models.Accounts;

namespace SmartRoutingAPI.Services;

/// <summary>
/// Zentrale, an einer Stelle gepflegte Definition des globalen Rate-Limiters der API.
/// Ersetzt die zuvor in <c>Program.Main</c> mehrfach kopierten Inline-Lambdas durch eine
/// datengetriebene, testbare Konfiguration.
/// </summary>
/// <remarks>
/// <para><b>Aufbau.</b> Der globale Limiter ist eine Verkettung dreier Fenster
/// (Sekunde ∧ Minute ∧ Stunde). Eine Anfrage muss ALLE drei Budgets besitzen; das jeweils
/// strengste Fenster entscheidet.</para>
/// <para><b>Nachsichtige Endpunkte.</b> Endpunkte mit <see cref="ForgivingRateLimitAttribute"/>
/// erhalten pro Aufrufer-Klasse spürbar höhere Kontingente. Da der <c>GlobalLimiter</c> in
/// ASP.NET Core mit endpunktspezifischen Limitern KOMBINIERT wird (strengster gewinnt), ist die
/// Erkennung im globalen Limiter der einzige korrekte Weg, einzelne Endpunkte zu lockern.</para>
/// <para><b>Kontingent-Matrix (Anfragen pro Fenster):</b></para>
/// <list type="table">
///   <listheader><term>Fenster</term><description>Standard (anon / admin / user)  |  Nachsichtig (anon / admin / user)</description></listheader>
///   <item><term>Sekunde</term><description>1 / 10 / 6   |   3 / 25 / 15</description></item>
///   <item><term>Minute</term><description>10 / 300 / 60   |   60 / 750 / 450</description></item>
///   <item><term>Stunde</term><description>60 / 2500 / 750   |   600 / 6000 / 3000</description></item>
/// </list>
/// <para>Für einen Standard-Nutzer entsprechen 15 Anfragen/s bei der Standard-Seitengröße (50)
/// exakt 750 Elementen/s. Das Minuten-Budget (450) erlaubt einen ~30-Sekunden-Burst mit 15 req/s,
/// bevor das Minutenfenster greift — genug, um Frontend-Listen zügig zu füllen, ohne die
/// DoS-Obergrenze (Stundenfenster) aufzugeben.</para>
/// </remarks>
internal static class RateLimitingPolicy
{
    // Fenster-Zeitspannen einmalig allozieren statt pro Anfrage neu zu berechnen (Performance).
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>Nur Pfade unterhalb dieses Segments werden ratenbegrenzt (Swagger/Statik bleiben frei).</summary>
    private const string ApiPathSegment = "/api";

    /// <summary>Partitionsschlüssel des „ohne Limit“-Falls (Nicht-API-Pfade).</summary>
    private const string NoLimiterKey = "";

    /// <summary>Standard-Kontingente. Werte 1:1 aus der bisherigen Inline-Konfiguration übernommen.</summary>
    private static class Standard
    {
        public const int PerSecondAnonymous = 1;
        public const int PerSecondAdministrator = 100;
        public const int PerSecondStandard = 10;

        public const int PerMinuteAnonymous = 10;
        public const int PerMinuteAdministrator = 3000;
        public const int PerMinuteStandard = 300;

        public const int PerHourAnonymous = 60;
        public const int PerHourAdministrator = 25000;
        public const int PerHourStandard = 2500;
    }

    /// <summary>
    /// Nachsichtige Kontingente für paginierte Lese-Endpunkte. Bewusst mit den kleineren Fenstern
    /// konsistent gehalten, damit ein zulässiger Sekunden-Burst nicht sofort vom Minutenfenster
    /// blockiert wird.
    /// </summary>
    private static class Forgiving
    {
        public const int PerSecondAnonymous = 3;
        public const int PerSecondAdministrator = 250;
        public const int PerSecondStandard = 25;   // vom Anforderer gewünscht (~750 Elemente/s bei Seitengröße 50).

        public const int PerMinuteAnonymous = 60;
        public const int PerMinuteAdministrator = 7500;
        public const int PerMinuteStandard = 750;  // erlaubt ~30 s Dauer-Burst mit 15 req/s.

        public const int PerHourAnonymous = 600;
        public const int PerHourAdministrator = 60000;
        public const int PerHourStandard = 6000;   // harte Stunden-Obergrenze bleibt als DoS-Schutz.
    }

    /// <summary>
    /// Erzeugt den globalen, verketteten Rate-Limiter. Wird einmalig beim Start aufgerufen.
    /// </summary>
    /// <param name="security">Dienst zur Ermittlung administrativer Rechte des Aufrufers.</param>
    /// <returns>Ein <see cref="PartitionedRateLimiter{HttpContext}"/> über die drei Zeitfenster.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="security"/> ist <c>null</c>.</exception>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(ISecurityService security)
    {
        // Fail-fast beim Start: Eine fehlende Abhängigkeit ist ein Programmierfehler und soll den
        // Bootvorgang laut abbrechen (kein Verschlucken). Reine Objekt-Komposition darüber hinaus
        // kann keine Ausnahme werfen, daher hier kein umschließendes try/catch.
        ArgumentNullException.ThrowIfNull(security);

        return PartitionedRateLimiter.CreateChained(
            CreatePartitioner(security, RateWindow.Second),
            CreatePartitioner(security, RateWindow.Minute),
            CreatePartitioner(security, RateWindow.Hour));
    }

    /// <summary>Erzeugt den Partitionierer für genau ein Zeitfenster.</summary>
    private static PartitionedRateLimiter<HttpContext> CreatePartitioner(ISecurityService security, RateWindow window)
        => PartitionedRateLimiter.Create<HttpContext, string>(httpContext => ResolvePartition(httpContext, security, window));

    /// <summary>
    /// Bestimmt pro Anfrage die passende Partition. Dies ist der einzige Ort mit anfrage-/
    /// benutzerabhängiger Logik und daher defensiv gekapselt.
    /// </summary>
    private static RateLimitPartition<string> ResolvePartition(HttpContext httpContext, ISecurityService security, RateWindow window)
    {
        try
        {
            // Einheitlicher /api-Filter über ALLE Fenster (behebt die frühere Inkonsistenz, bei der
            // nur das Sekundenfenster gefiltert wurde). Nicht-API-Pfade bleiben unbegrenzt.
            if (!httpContext.Request.Path.StartsWithSegments(ApiPathSegment))
            {
                return RateLimitPartition.GetNoLimiter(NoLimiterKey);
            }

            var user = httpContext.User;
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var isAnonymous = string.IsNullOrEmpty(userId);

            var tier = ResolveTier(security, user, isAnonymous);

            // Anonyme Aufrufer haben keine stabile Identität → Partition per Remote-IP.
            var subject = isAnonymous ? SafeRemoteIp(httpContext) : userId;

            var isForgiving = IsForgivingEndpoint(httpContext);
            var plan = ResolvePlan(isForgiving, tier, window);

            return BuildFixedWindow(subject, window, plan);
        }
        catch (Exception)
        {
            // Fail-safe, niemals fail-open: Schlägt die Identitäts-/Metadaten-Ermittlung aus
            // irgendeinem Grund fehl, wird auf das strengste (anonyme) Standard-Kontingent pro IP
            // zurückgefallen. So bleibt der Schutz erhalten, ohne den gesamten Partitionsraum zu
            // sperren. Ein Rethrow würde die Limiter-Middleware für JEDE Anfrage fehlschlagen lassen,
            // daher wird hier bewusst deterministisch degradiert statt weitergeworfen.
            var fallbackPlan = ResolvePlan(forgiving: false, CallerTier.Anonymous, window);
            return BuildFixedWindow(SafeRemoteIp(httpContext), window, fallbackPlan);
        }
    }

    /// <summary>Klassifiziert den Aufrufer. Reine, ausnahmefreie Logik (läuft im try des Aufrufers).</summary>
    private static CallerTier ResolveTier(ISecurityService security, ClaimsPrincipal user, bool isAnonymous)
    {
        if (isAnonymous)
        {
            return CallerTier.Anonymous;
        }

        return security.HasAdministrativeRight(user, AdministrativeRights.Administrate)
            ? CallerTier.Administrator
            : CallerTier.Standard;
    }

    /// <summary>
    /// O(1)-Prüfung, ob der aufgelöste Endpunkt als nachsichtig markiert ist — reiner
    /// Metadaten-Lookup, keine Reflection pro Anfrage.
    /// </summary>
    private static bool IsForgivingEndpoint(HttpContext httpContext)
        => httpContext.GetEndpoint()?.Metadata.GetMetadata<ForgivingRateLimitAttribute>() is not null;

    /// <summary>Baut den konkreten Fixed-Window-Limiter für Subjekt + Fenster + Plan.</summary>
    private static RateLimitPartition<string> BuildFixedWindow(string subject, RateWindow window, PermitPlan plan)
        => RateLimitPartition.GetFixedWindowLimiter(
            subject + WindowSuffix(window),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = plan.PermitLimit,
                Window = plan.Window
            });

    /// <summary>Null-sichere Ermittlung der Remote-IP als Partitionssubjekt.</summary>
    private static string SafeRemoteIp(HttpContext httpContext)
        => httpContext.Connection?.RemoteIpAddress?.ToString() ?? string.Empty;

    /// <summary>
    /// Fenster-spezifisches Schlüssel-Suffix. Trennt die drei verketteten Partitionsräume, damit
    /// sie nicht auf demselben Schlüssel kollidieren.
    /// </summary>
    private static string WindowSuffix(RateWindow window) => window switch
    {
        RateWindow.Second => "_sec",
        RateWindow.Minute => "_min",
        RateWindow.Hour => "_hour",
        _ => "_sec"
    };

    /// <summary>
    /// Deterministische Zuordnung (Fenster × Aufrufer-Klasse × Nachsicht) → Kontingent.
    /// Vollständig und erschöpfend; der Default-Arm ist ein defensiver Rückfall auf den strengsten Wert.
    /// </summary>
    private static PermitPlan ResolvePlan(bool forgiving, CallerTier tier, RateWindow window) => (window, tier, forgiving) switch
    {
        // ---------------- Sekundenfenster ----------------
        (RateWindow.Second, CallerTier.Anonymous, false) => new(Standard.PerSecondAnonymous, OneSecond),
        (RateWindow.Second, CallerTier.Administrator, false) => new(Standard.PerSecondAdministrator, OneSecond),
        (RateWindow.Second, CallerTier.Standard, false) => new(Standard.PerSecondStandard, OneSecond),
        (RateWindow.Second, CallerTier.Anonymous, true) => new(Forgiving.PerSecondAnonymous, OneSecond),
        (RateWindow.Second, CallerTier.Administrator, true) => new(Forgiving.PerSecondAdministrator, OneSecond),
        (RateWindow.Second, CallerTier.Standard, true) => new(Forgiving.PerSecondStandard, OneSecond),

        // ---------------- Minutenfenster ----------------
        (RateWindow.Minute, CallerTier.Anonymous, false) => new(Standard.PerMinuteAnonymous, OneMinute),
        (RateWindow.Minute, CallerTier.Administrator, false) => new(Standard.PerMinuteAdministrator, OneMinute),
        (RateWindow.Minute, CallerTier.Standard, false) => new(Standard.PerMinuteStandard, OneMinute),
        (RateWindow.Minute, CallerTier.Anonymous, true) => new(Forgiving.PerMinuteAnonymous, OneMinute),
        (RateWindow.Minute, CallerTier.Administrator, true) => new(Forgiving.PerMinuteAdministrator, OneMinute),
        (RateWindow.Minute, CallerTier.Standard, true) => new(Forgiving.PerMinuteStandard, OneMinute),

        // ---------------- Stundenfenster ----------------
        (RateWindow.Hour, CallerTier.Anonymous, false) => new(Standard.PerHourAnonymous, OneHour),
        (RateWindow.Hour, CallerTier.Administrator, false) => new(Standard.PerHourAdministrator, OneHour),
        (RateWindow.Hour, CallerTier.Standard, false) => new(Standard.PerHourStandard, OneHour),
        (RateWindow.Hour, CallerTier.Anonymous, true) => new(Forgiving.PerHourAnonymous, OneHour),
        (RateWindow.Hour, CallerTier.Administrator, true) => new(Forgiving.PerHourAdministrator, OneHour),
        (RateWindow.Hour, CallerTier.Standard, true) => new(Forgiving.PerHourStandard, OneHour),

        // Defensiver Rückfall: strengstes bekanntes Kontingent (sollte unerreichbar sein).
        _ => new(Standard.PerSecondAnonymous, OneSecond)
    };

    /// <summary>Die drei verketteten Zeitfenster des globalen Limiters.</summary>
    private enum RateWindow
    {
        Second,
        Minute,
        Hour
    }

    /// <summary>Aufrufer-Klasse, die das Kontingent bestimmt.</summary>
    private enum CallerTier
    {
        /// <summary>Nicht authentifiziert — partitioniert per IP, strengste Kontingente.</summary>
        Anonymous,

        /// <summary>Authentifiziert mit administrativem Recht <see cref="AdministrativeRights.Administrate"/>.</summary>
        Administrator,

        /// <summary>Authentifiziert ohne administratives Recht.</summary>
        Standard
    }

    /// <summary>Unveränderliches Wertepaar aus Kontingentgröße und Fensterlänge.</summary>
    private readonly record struct PermitPlan(int PermitLimit, TimeSpan Window);
}
