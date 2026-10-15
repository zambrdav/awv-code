using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <summary>
    /// Ergänzt punktgenaue Bedienzeiten an Sammelpunkten sowie Bedienzeiten an Abladestellen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Jobs.ServiceTimeSeconds</c> löst die bisher ausschließlich globale Bedienzeit je
    /// Sammelpunkt ab. Ein Glasplatz mit vier Behältern und schwieriger Zufahrt braucht mehr Zeit
    /// als ein einzelner Behälter am Straßenrand; da die Bedienzeit in die Schichtdauer eingeht,
    /// verschiebt eine falsche Annahme die gesamte Tourenplanung.
    /// </para>
    /// <para>
    /// <c>DeliveryDestinations.ServiceTimeSeconds</c> und
    /// <c>DeliveryDestinations.ServiceSecondsPerUnit</c> beschreiben den Aufwand einer Entleerung
    /// als festen Anteil zuzüglich eines mengenabhängigen Anteils. Das ist der maßgebliche Regler
    /// gegen unnötige Zwischenentleerungen: Solange eine Entleerung nahezu kostenlos erscheint, hat
    /// der Optimierer keinen Grund, das Fahrzeug vorher auszufüllen.
    /// </para>
    /// <para>
    /// Alle Spalten sind optional (<c>NULL</c>) und bedeuten dann „globale Vorgabe verwenden“.
    /// Rein additiv – es werden keine Daten verändert oder gelöscht.
    /// </para>
    /// </remarks>
    public partial class AddPerPointAndPerFacilityServiceTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceTimeSeconds",
                table: "Jobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceTimeSeconds",
                table: "DeliveryDestinations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ServiceSecondsPerUnit",
                table: "DeliveryDestinations",
                type: "double",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServiceSecondsPerUnit",
                table: "DeliveryDestinations");

            migrationBuilder.DropColumn(
                name: "ServiceTimeSeconds",
                table: "DeliveryDestinations");

            migrationBuilder.DropColumn(
                name: "ServiceTimeSeconds",
                table: "Jobs");
        }
    }
}
