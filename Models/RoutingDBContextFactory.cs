using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartRoutingAPI.Models;

/// <summary>
/// Erzeugt den <see cref="RoutingDBContext"/> für die EF-Core-Werkzeuge
/// (<c>dotnet ef migrations</c>, <c>dotnet ef dbcontext</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ohne diese Fabrik baut das Werkzeug die vollständige Anwendung auf, um an den Kontext zu
/// kommen. Das ist aus zwei Gründen unbrauchbar:
/// </para>
/// <list type="number">
///   <item><description>
///     Die Anwendung ermittelt die Serverversion über <c>ServerVersion.AutoDetect</c> und benötigt
///     dafür eine <em>erreichbare</em> Datenbank. Eine Migration zu erzeugen setzt damit den
///     laufenden Betrieb voraus – auf einem Entwicklungsrechner ohne Netzzugang oder in einer
///     Build-Pipeline schlägt das fehl.
///   </description></item>
///   <item><description>
///     Der Anwendungsstart prüft Geheimnisse und CORS-Einstellungen und verweigert den Start bei
///     fehlender Konfiguration. Diese Prüfungen sind für die Erzeugung einer Migration
///     bedeutungslos, verhindern sie aber.
///   </description></item>
/// </list>
/// <para>
/// Die hier verwendete Zeichenfolge wird nie zum Verbinden benutzt: Für die Erzeugung von
/// Migrationen ist ausschließlich die <em>Version</em> des Anbieters relevant, weil sie bestimmt,
/// welche SQL-Merkmale zur Verfügung stehen. Damit sind erzeugte Migrationen reproduzierbar und
/// nicht von der Version des gerade erreichbaren Servers abhängig.
/// </para>
/// </remarks>
public sealed class RoutingDBContextFactory : IDesignTimeDbContextFactory<RoutingDBContext>
{
    /// <summary>
    /// Die für Migrationen zugrunde gelegte MySQL-Version. Muss zur eingesetzten Serverversion
    /// passen; eine Änderung ist eine bewusste Entscheidung und gehört in die Versionsverwaltung.
    /// </summary>
    private static readonly MySqlServerVersion TargetServerVersion = new(new Version(8, 0, 36));

    /// <inheritdoc />
    public RoutingDBContext CreateDbContext(string[] args)
    {
        try
        {
            var options = new DbContextOptionsBuilder<RoutingDBContext>()
                .UseMySql("Server=localhost;Database=SmartCityRouting", TargetServerVersion)
                .Options;

            return new RoutingDBContext(options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Der Entwurfszeit-Kontext konnte nicht erzeugt werden.", ex);
        }
    }
}
