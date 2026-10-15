using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using SmartRoutingAPI.Configuration;
using SmartRoutingAPI.Models;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Services;
using Swashbuckle.AspNetCore.Filters;
using Microsoft.AspNetCore.Hosting;

namespace SmartRoutingAPI;

public class Program
{
    static string GetUserEndPoint(HttpContext context) =>
   $"User {context.User.Identity?.Name ?? "Anonymous"} endpoint:{context.Request.Path}"
   + $" {context.Connection.RemoteIpAddress}";
    public static void Main(string[] args)
    {
        var _sec = new SecurityService();
        var builder = WebApplication.CreateBuilder(args);

#if DEBUG && false
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(5000); // HTTP
            serverOptions.ListenAnyIP(5001, listenOptions =>
            {
                listenOptions.UseHttps(); // HTTPS
            });
        });
#endif

        var jwtIssuer = builder.Configuration.GetSection("Jwt:Issuer").Get<string>();
        var jwtKey = builder.Configuration.GetSection("Jwt:Key").Get<string>();

        if (string.IsNullOrEmpty(jwtIssuer) || string.IsNullOrEmpty(jwtKey))
        {
            throw new InvalidOperationException(
                "Jwt:Issuer und Jwt:Key müssen gesetzt sein. Ablage über Umgebungsvariablen "
                + "(Jwt__Key) oder 'dotnet user-secrets', nicht in appsettings.json.");
        }

        // Ein HMAC-SHA256-Schlüssel unterhalb von 32 Byte schwächt die Signatur unter die
        // Ausgabelänge des Hashs und ist damit angreifbar. Der Start wird verweigert, weil ein
        // schwacher Schlüssel das Fälschen beliebiger Tokens – einschließlich Administrator-Rechten
        // – erlaubt und im Betrieb nicht auffällt.
        if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Key muss mindestens 32 Byte lang sein (HMAC-SHA256).");
        }

        RejectPlaceholderSecrets(builder.Configuration);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtIssuer,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        });

        builder.Services.AddScoped<ISecurityService, SecurityService>();

        // ----------------------------------------------------------------------------------
        // Routing-Stack
        //
        // Zwei kostenfreie, selbst gehostete Bausteine:
        //   * Valhalla liefert Fahrzeit- UND Entfernungsmatrizen sowie die Geometrie – jeweils mit
        //     LKW-Kostenmodell und, falls freigegeben, den Sonderrechten des Hoheitlich-Modus.
        //   * OR-Tools löst das Sammeltourenproblem mit Zwischenentleerung und frei wählbaren
        //     Abladestellen, mit CO2e als Zielfunktion und der Schichtdauer als eigener
        //     Nebenbedingung.
        // ----------------------------------------------------------------------------------
        builder.Services
            .AddOptions<SmartRoutingAPI.Services.Routing.RoutingOptions>()
            .Bind(builder.Configuration.GetSection(SmartRoutingAPI.Services.Routing.RoutingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => !options.Sovereign.Enabled || !string.IsNullOrWhiteSpace(options.Sovereign.LegalBasis),
                "Routing:Sovereign:LegalBasis muss gesetzt sein, wenn Sonderrechte aktiviert sind.")
            // Eine fehlerhafte Routing-Konfiguration soll den Start verhindern und nicht erst beim
            // ersten Optimierungslauf zu unplausiblen Routen führen.
            .ValidateOnStart();

        var routingOptions = builder.Configuration
            .GetSection(SmartRoutingAPI.Services.Routing.RoutingOptions.SectionName)
            .Get<SmartRoutingAPI.Services.Routing.RoutingOptions>()
            ?? new SmartRoutingAPI.Services.Routing.RoutingOptions();

        // Valhalla is operated per installation, so its address has no default in source. Parsed
        // once here so a missing or malformed value stops the start instead of surfacing on the
        // first optimisation run, and so all typed clients share one validated address.
        var valhallaBaseUri = DeploymentSettings.RequireServiceRootUrl(
            routingOptions.Valhalla.BaseUrl, "Routing:Valhalla:BaseUrl");

        // Das Valhalla-Kostenmodell wird EINMAL je Prozess aufgebaut und von Planung UND
        // Fahrer-Navigation gemeinsam genutzt. Zwei getrennte Modelle würden bedeuten, dass der
        // Fahrer über Straßen geführt wird, die der Planer nie bewertet hat – geplante und
        // gefahrene Kilometer wären damit nicht mehr vergleichbar.
        builder.Services.AddSingleton<SmartRoutingAPI.Services.Routing.ValhallaCostingProfile>();

        builder.Services.AddHttpClient<
            SmartRoutingAPI.Services.Routing.IRoadNetworkService,
            SmartRoutingAPI.Services.Routing.ValhallaRoadNetworkService>(client =>
        {
            client.BaseAddress = valhallaBaseUri;
            client.Timeout = TimeSpan.FromSeconds(routingOptions.Valhalla.TimeoutSeconds);
        });

        builder.Services.AddSingleton<
            SmartRoutingAPI.Services.Routing.IEmissionModel,
            SmartRoutingAPI.Services.Routing.HbefaRefuseTruckEmissionModel>();

        builder.Services.AddScoped<
            SmartRoutingAPI.Services.Routing.ICollectionPlanner,
            SmartRoutingAPI.Services.Routing.OrToolsCollectionPlanner>();

        // Die Optimierung läuft in einem Hintergrund-Worker, damit kein Anfrage-Thread darauf wartet.
        builder.Services.AddScoped<RouteOptimizationService>();
        builder.Services.AddSingleton<IRouteOptimizationQueue, RouteOptimizationQueue>();
        builder.Services.AddHostedService<RouteOptimizationWorker>();

        // Valhalla (Meili) Map-Matching: eigener typisierter Client. Wird für den Abgleich echter
        // GPS-Spuren mit dem Straßennetz genutzt und liefert damit die Vergleichsbasis für die
        // CO2-Bilanz historischer Touren.
        //
        // Valhalla:BaseUrl is an optional override for installations that run map matching on a
        // separate instance; by default the single Routing:Valhalla:BaseUrl serves all clients.
        var mapMatchingBaseUri = DeploymentSettings.OptionalServiceRootUrl(
                builder.Configuration.GetValue<string>("Valhalla:BaseUrl"), "Valhalla:BaseUrl")
            ?? valhallaBaseUri;
        var valhallaTimeoutSeconds = builder.Configuration.GetValue<int?>("Valhalla:TimeoutSeconds") ?? 30;

        builder.Services.AddHttpClient<IValhallaService, ValhallaService>(client =>
        {
            client.BaseAddress = mapMatchingBaseUri;
            client.Timeout = TimeSpan.FromSeconds(valhallaTimeoutSeconds);
        });

        // ----------------------------------------------------------------------------------
        // Fahrer-Navigation (Live-Routing)
        //
        // Schließt die Lücke zwischen der geplanten Tour und dem Fahrzeug: Fortschritt gegen die
        // geplante Strecke, Erkennung einer Abweichung und Abbiegehinweise zum nächsten Halt.
        //
        // Zwei bewusst getrennte Bausteine:
        //   * RouteProgressCalculator ist REIN RECHNERISCH. Er projiziert die gemeldete Position
        //     senkrecht auf die gespeicherte Geometrie der Route. Kein Netzaufruf, kein Zustand,
        //     Laufzeit im Mikrosekundenbereich. Für die Frage "ist dieses Fahrzeug noch auf Route
        //     X?" ist das die exakte Antwort – ein Abgleich gegen das Gesamtstraßennetz (Meili)
        //     beantwortet eine andere Frage und wäre dabei ungenauer.
        //   * ValhallaNavigationService liefert die Abbiegehinweise. Er ruft /route mit
        //     directions_type = "instructions" auf – im Gegensatz zur Planung, die "none" nutzt –
        //     und verwendet über ValhallaCostingProfile dasselbe LKW-Kostenmodell.
        //
        // Der Zeitüberschreitungswert ist bewusst der der Planung: Eine Neuberechnung während der
        // Fahrt muss innerhalb weniger Sekunden vorliegen, benötigt aber dieselbe Instanz.
        // ----------------------------------------------------------------------------------
        builder.Services.AddSingleton<SmartRoutingAPI.Services.Navigation.RouteProgressCalculator>();

        builder.Services.AddHttpClient<
            SmartRoutingAPI.Services.Navigation.INavigationService,
            SmartRoutingAPI.Services.Navigation.ValhallaNavigationService>(client =>
        {
            client.BaseAddress = valhallaBaseUri;
            client.Timeout = TimeSpan.FromSeconds(routingOptions.Valhalla.TimeoutSeconds);
        });

        // Add services to the container.

        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            // Antwort-DTOs sind bauartbedingt zyklenfrei; IgnoreCycles ist nur noch ein Netz.
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
            options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

            // Unbekannte Felder im Anfragekörper werden abgewiesen (HTTP 400) statt ignoriert.
            //
            // Dies ist bewusst eine strenge Einstellung. Der stillschweigende Umgang mit
            // unbekannten Feldern war die Ursache eines Datenverlusts, der monatelang unentdeckt
            // blieb: Der nächtliche Import sendete weiterhin DeliveryLatitude und
            // DeliveryLongitude, nachdem das Feld aus dem Vertrag entfernt worden war. Die API
            // antwortete mit 200 OK und verwarf die Werte. Ein Aufrufer, der veraltete Felder
            // sendet, muss davon erfahren.
            options.JsonSerializerOptions.UnmappedMemberHandling =
                System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        });

        // RFC7807 ProblemDetails for every error response (§4.8). Replaces ad-hoc BadRequest+stacktrace.
        builder.Services.AddProblemDetails();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();

        // ----------------------------------------------------------------------------------
        // Datenbank
        //
        // Die Serverversion wird aus der Konfiguration gelesen und nicht mehr über
        // ServerVersion.AutoDetect ermittelt. AutoDetect stellt während der Registrierung der
        // Dienste – also synchron im Anwendungsstart – eine echte Verbindung her. Ist die Datenbank
        // in diesem Moment nicht erreichbar, startet die Anwendung nicht, und das erzeugte Modell
        // hängt von der Version des gerade erreichbaren Servers ab. Beides ist für einen
        // reproduzierbaren Betrieb untauglich.
        // ----------------------------------------------------------------------------------
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection ist nicht gesetzt. Ablage über die "
                + "Umgebungsvariable ConnectionStrings__DefaultConnection oder 'dotnet user-secrets'.");
        }

        var configuredServerVersion = builder.Configuration.GetValue<string>("Database:ServerVersion");
        var serverVersion = string.IsNullOrWhiteSpace(configuredServerVersion)
            ? new MySqlServerVersion(new Version(8, 0, 36))
            : new MySqlServerVersion(Version.Parse(configuredServerVersion));

        builder.Services.AddDbContext<RoutingDBContext>(options => options.UseMySql(
            connectionString,
            serverVersion,

            // Kurzzeitige Netz- oder Datenbankaussetzer sollen nicht zu einem Fehler beim Aufrufer
            // führen. Zulässig, weil die Anwendung keine selbst gesteuerten Transaktionen über
            // mehrere SaveChanges-Aufrufe hinweg verwendet – nur dann wäre ein Wiederholungsversuch
            // unzulässig.
            mySqlOptions => mySqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null)));

        // Operator name, contact and legal links differ per installation and are published to every
        // API consumer, so they come from configuration. Built eagerly so a malformed link fails
        // the start rather than the first Swagger request.
        var openApiInfo = (builder.Configuration
                .GetSection(OpenApiDocumentOptions.SectionName)
                .Get<OpenApiDocumentOptions>() ?? new OpenApiDocumentOptions())
            .ToOpenApiInfo("v1");

        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", openApiInfo);

            options.ExampleFilters();

            // using System.Reflection;
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
        });

        builder.Services.AddSwaggerExamplesFromAssemblies(Assembly.GetEntryAssembly());

        // Add the RateLimiter
        builder.Services.AddRateLimiter(options =>
        {
            options.OnRejected = (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("Microsoft.AspNetCore.RateLimitingMiddleware")
                    .LogWarning("OnRejected: {GetUserEndPoint}", GetUserEndPoint(context.HttpContext));

                return new ValueTask();
            };

            // Der globale Limiter ist zentral und endpunkt-bewusst definiert (siehe RateLimitingPolicy):
            // paginierte Lese-Endpunkte mit [ForgivingRateLimit] erhalten großzügigere Kontingente,
            // alle übrigen /api-Pfade behalten die bisherigen strengen Standardwerte.
            options.GlobalLimiter = RateLimitingPolicy.CreateGlobalLimiter(_sec);
        });

        // ----------------------------------------------------------------------------------
        // CORS
        //
        // Die Vorversion erlaubte AllowAnyOrigin + AllowAnyMethod + AllowAnyHeader. Da die API
        // Bearer-Token verwendet, ist das keine unmittelbare Rechteumgehung – wohl aber die
        // Freigabe, dass jede beliebige Website Anfragen an diese API formulieren darf. Für eine
        // Anwendung mit bekanntem Frontend gibt es dafür keinen Grund.
        //
        // Die erlaubten Ursprünge stehen in Cors:AllowedOrigins. Fehlt der Eintrag, wird nur die
        // lokale Entwicklung zugelassen; in Produktion ist eine leere Liste ein Konfigurationsfehler
        // und wird beim Start abgewiesen.
        // ----------------------------------------------------------------------------------
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        if (allowedOrigins.Length == 0)
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins muss außerhalb der Entwicklung mindestens einen Ursprung "
                    + "enthalten. Wildcard-CORS wird nicht mehr unterstützt.");
            }

            allowedOrigins = new[] { "http://localhost:3000", "https://localhost:3000" };
        }

        DeploymentSettings.ValidateCorsOrigins(allowedOrigins);

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()));

        // Anonymous callers are rate-limited per client address. Behind a reverse proxy that address
        // is the proxy's own, so all anonymous callers would share one partition. Only proxies listed
        // in Security:TrustedProxyAddresses may rewrite it; the loopback entries trusted by default
        // are cleared so that no unlisted peer can spoof X-Forwarded-For and mint unlimited
        // partitions. With an empty list nothing is registered, leaving the hosting defaults intact:
        // IIS in-process reports the client address itself, and IIS out-of-process depends on its
        // own loopback forwarding, which clearing the defaults would break.
        var trustedProxyAddresses = DeploymentSettings.ReadTrustedProxyAddresses(builder.Configuration);

        if (trustedProxyAddresses.Count > 0)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var proxyAddress in trustedProxyAddresses)
                {
                    options.KnownProxies.Add(proxyAddress);
                }
            });
        }

        var app = builder.Build();

        // Global error handling → RFC7807 ProblemDetails. Unhandled exceptions become 500 without
        // leaking messages/stack traces (§4.8). Must be the outermost middleware.
        app.UseExceptionHandler();

        if (trustedProxyAddresses.Count > 0)
        {
            // Must precede CORS, authentication and the rate limiter, which all read the client address.
            app.UseForwardedHeaders();
        }

        app.UseCors();

        // Migrate the database.
        app.MigrateDatabase<RoutingDBContext>();

        // Configure the HTTP request pipeline.
        app.UseSwagger();
        app.UseSwaggerUI(C => C.EnableDeepLinking());

        //app.UseHttpsRedirection();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseRateLimiter();

        app.MapControllers();

        app.Run();
    }

    /// <summary>
    /// Template placeholders that must never become the password of the bootstrap administrator.
    /// </summary>
    /// <remarks>
    /// Configuration templates commonly ship these values. Accepting one would create a fully
    /// privileged account whose password is publicly known, so the start is refused instead.
    /// </remarks>
    private static readonly string[] PlaceholderSecrets =
    {
        "CHANGE_ME"
    };

    /// <summary>
    /// Refuses the start while the bootstrap administrator password is still a template placeholder.
    /// </summary>
    /// <param name="configuration">The loaded configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>DefaultAccount:Password</c> is a placeholder, or the configuration could not be read.
    /// </exception>
    private static void RejectPlaceholderSecrets(IConfiguration configuration)
    {
        try
        {
            var defaultPassword = configuration["DefaultAccount:Password"];

            if (!string.IsNullOrEmpty(defaultPassword)
                && PlaceholderSecrets.Contains(defaultPassword, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "DefaultAccount:Password ist noch ein Platzhalter. Bitte über Umgebungsvariable "
                    + "DefaultAccount__Password oder 'dotnet user-secrets' setzen.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Eine fehlerhafte Konfigurationsquelle darf nicht als "Geheimnis in Ordnung" gelten.
            throw new InvalidOperationException("Die Geheimnis-Prüfung ist fehlgeschlagen.", ex);
        }
    }
}