using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <summary>
    /// Ergänzt <c>Jobs.Data</c> — beliebige clientseitige Rohdaten (üblicherweise JSON) je
    /// Sammelpunkt, die der Server an keiner Stelle interpretiert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rein additiv: eine einzelne nullable Spalte auf einer bestehenden Tabelle, ohne
    /// Änderung, Migration oder Löschung irgendeiner vorhandenen Spalte oder Zeile. Bestehende
    /// Datensätze erhalten automatisch <c>NULL</c> (die vorgesehene Vorgabe); es ist keine
    /// Datenübernahme nötig, und <c>Down</c> entfernt ausschließlich die neue Spalte wieder.
    /// </para>
    /// <para>
    /// Spaltentyp bewusst <c>TEXT</c> statt des sonst aus <c>[MaxLength]</c> abgeleiteten
    /// <c>VARCHAR</c>: VARCHAR-Bytes zählen bei InnoDB in die feste 65.535-Byte-Zeilenobergrenze
    /// über alle Spalten der Tabelle hinweg, TEXT wird davon unabhängig (ggf. off-page)
    /// gespeichert. Die Anwendungsvalidierung begrenzt Eingaben zusätzlich auf 16.000 Zeichen
    /// (<see cref="Models.Jobs.Job.MaxDataLength"/>) — selbst im ungünstigsten Fall (4 Byte pro
    /// Zeichen bei <c>utf8mb4</c>) sind das höchstens 64.000 Byte, sicher innerhalb der
    /// 65.535-Byte-Obergrenze von <c>TEXT</c>.
    /// </para>
    /// </remarks>
    public partial class AddJobDataField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "Jobs",
                type: "text",
                maxLength: 16000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Data",
                table: "Jobs");
        }
    }
}
