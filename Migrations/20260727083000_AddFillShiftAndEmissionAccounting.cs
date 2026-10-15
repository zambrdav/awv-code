using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <summary>
    /// Ergänzt das Kennzeichen <c>FillShift</c> sowie die Felder der CO2e-Bilanz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FillShift</c> steuert die Zielsetzung der Optimierung: Vorgabe <c>false</c> plant nach
    /// Emissionseffizienz, <c>true</c> füllt die Schicht bewusst aus. Der Standardwert <c>false</c>
    /// stellt sicher, dass bestehende Routen die effizienzorientierte Planung erhalten.
    /// </para>
    /// <para>
    /// <c>Co2eGrams</c>, <c>CollectedUnits</c> und <c>EmissionFactorSource</c> machen die
    /// Emissionsbilanz je Route dauerhaft prüfbar. Die Herkunft der Faktoren wird bewusst
    /// mitgespeichert: eine Kennzahl ohne dokumentierte Grundlage ist für einen Fördernachweis
    /// wertlos.
    /// </para>
    /// <para>Rein additiv – es werden keine Daten verändert oder gelöscht.</para>
    /// </remarks>
    public partial class AddFillShiftAndEmissionAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FillShift",
                table: "Routes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Co2eGrams",
                table: "Routes",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CollectedUnits",
                table: "Routes",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmissionFactorSource",
                table: "Routes",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "FillShift",
                table: "FrequencyRoutes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FillShift",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "EmissionFactorSource",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "CollectedUnits",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "Co2eGrams",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "FillShift",
                table: "Routes");
        }
    }
}
