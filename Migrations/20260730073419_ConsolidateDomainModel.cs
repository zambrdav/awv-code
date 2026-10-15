using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <summary>
    /// Bereinigt das Datenmodell: entfernt die Altlasten der VROOM-Generation, behebt eine
    /// falsch abgeleitete Beziehung und benennt irreführende Spalten um.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Diese Migration wurde von Hand nachgearbeitet.</b> Die von den EF-Werkzeugen erzeugte
    /// Fassung enthielt vier Fehler, von denen zwei stillschweigend Daten zerstört hätten. Die
    /// Werkzeuge erkennen Umbenennungen ausschließlich über die Reihenfolge der Spalten und
    /// besitzen keine Kenntnis der Bedeutung eines Feldes:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>Steps.Distance</c> sollte in <c>Steps.JobId</c> umbenannt werden. Aus der gefahrenen
    ///     Strecke (etwa 5631 Meter) wäre damit ein Fremdschlüssel auf einen Sammelpunkt geworden.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Steps.WaitingTime</c> sollte in <c>Steps.TravelTimeFromStart</c> umbenannt werden.
    ///     Die Wartezeit war ausnahmslos null, die tatsächliche Fahrzeit stand in <c>Duration</c> –
    ///     und <c>Duration</c> wäre gelöscht worden. Alle Fahrzeiten wären null geworden.
    ///   </description></item>
    ///   <item><description>
    ///     Die Zuordnung Fraktion ↔ Abladestelle wäre beim Wechsel von <c>Skills.DeliveryDestinationId</c>
    ///     auf die Verknüpfungstabelle verloren gegangen.
    ///   </description></item>
    ///   <item><description>
    ///     Die Zahlenwerte von <c>Steps.StepType</c> wären unverändert geblieben, obwohl die
    ///     Aufzählung neu belegt ist. Ein gespeicherter Sammelhalt (vormals 2) wäre danach als
    ///     Entleerung gelesen worden.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Zusätzlich fehlte das Füllen von <c>Steps.Sequence</c>. Ohne dieses hätten alle Halte den
    /// Wert 0 erhalten und der neue eindeutige Index <c>(RouteId, Sequence)</c> wäre für jede Route
    /// mit mehr als einem Halt fehlgeschlagen – die Migration wäre also mitten im Lauf abgebrochen.
    /// </para>
    /// </remarks>
    public partial class ConsolidateDomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // =====================================================================================
            // 1) Tote Tabellen entfernen
            //
            //    Deliveries  – fester Ablieferungsort je Sammelpunkt. Ohne Wirkung auf die Planung
            //                  und ohne Löschweg: jede Aktualisierung eines Sammelpunkts hinterließ
            //                  eine verwaiste Zeile.
            //    Violations  – Nebenbedingungsverletzungen des VROOM-Modells. Kein Codepfad hat je
            //                  eine Zeile geschrieben.
            //    Stats       – Kacheltabelle ohne jeden Schreibpfad; dauerhaft leer.
            // =====================================================================================
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_DeliveryDestinationId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "DeliveryDestinationId",
                table: "Jobs");

            migrationBuilder.DropTable(name: "Deliveries");
            migrationBuilder.DropTable(name: "Violations");
            migrationBuilder.DropTable(name: "Stats");

            // =====================================================================================
            // 2) Fraktion <-> Abladestelle: 1:n wird zu n:m
            //
            //    Bisher lag der Fremdschlüssel auf Skills. Eine Fraktion konnte damit nur an einer
            //    einzigen Abladestelle angenommen werden; "beide Werke nehmen Weißglas" war nicht
            //    darstellbar. Die bestehenden Zuordnungen werden vollständig übernommen.
            // =====================================================================================
            migrationBuilder.CreateTable(
                name: "DeliveryDestinationSkill",
                columns: table => new
                {
                    DeliveryDestinationsId = table.Column<int>(type: "int", nullable: false),
                    SkillsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryDestinationSkill", x => new { x.DeliveryDestinationsId, x.SkillsId });
                    table.ForeignKey(
                        name: "FK_DeliveryDestinationSkill_DeliveryDestinations_DeliveryDestin~",
                        column: x => x.DeliveryDestinationsId,
                        principalTable: "DeliveryDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliveryDestinationSkill_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryDestinationSkill_SkillsId",
                table: "DeliveryDestinationSkill",
                column: "SkillsId");

            // Bestandsdaten übernehmen, BEVOR die alte Spalte entfernt wird.
            migrationBuilder.Sql(@"
INSERT INTO `DeliveryDestinationSkill` (`DeliveryDestinationsId`, `SkillsId`)
SELECT `DeliveryDestinationId`, `Id`
FROM `Skills`
WHERE `DeliveryDestinationId` IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Skills_DeliveryDestinations_DeliveryDestinationId",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_Skills_DeliveryDestinationId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "DeliveryDestinationId",
                table: "Skills");

            // =====================================================================================
            // 3) Sammelpunkte
            // =====================================================================================

            // Wirkungsloses, dreifach mehrdeutiges Kennzeichen. Es wurde als Migration
            // "ManualPriorityField" angelegt, als Berechtigung der Fahrer-Oberfläche beschrieben und
            // vom Import als "kein Sensor vorhanden" gefüllt. Kein Codepfad hat es je gelesen.
            migrationBuilder.DropColumn(
                name: "AllowManualAssignment",
                table: "Jobs");

            // Die Fraktion eines Sammelpunkts wird optional, und das Löschen einer Fraktion darf
            // nicht länger hunderte Sammelpunkte mitlöschen (Cascade -> Restrict).
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Skills_SkillId",
                table: "Jobs");

            migrationBuilder.AlterColumn<int>(
                name: "SkillId",
                table: "Jobs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Skills_SkillId",
                table: "Jobs",
                column: "SkillId",
                principalTable: "Skills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Fremdsysteme gleichen ausschließlich über die externe Kennung ab.
            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ExternalReference",
                table: "Jobs",
                column: "ExternalReference");

            // =====================================================================================
            // 4) Routen
            // =====================================================================================

            // Die Reihenfolge der Sammelpunkte lag zusätzlich als JSON in einer longtext-Spalte.
            // Dieselbe Information existierte dreifach (Menge, Reihenfolge, Schrittfolge) ohne
            // jede Zusicherung der Übereinstimmung. Maßgeblich ist ab jetzt die Schrittliste.
            migrationBuilder.DropColumn(
                name: "JobsOrderIdList",
                table: "Routes");

            // Wurde ausnahmslos auf null gesetzt.
            migrationBuilder.DropColumn(
                name: "WaitingTime",
                table: "Routes");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_StartDate_EndDate",
                table: "Routes",
                columns: new[] { "StartDate", "EndDate" });

            // =====================================================================================
            // 5) Halte einer Route
            //
            //    Reihenfolge der Schritte ist bewusst: erst neue Spalten anlegen, dann Bestand
            //    übernehmen, erst danach Altspalten entfernen. Eine Umbenennung wird nur dort
            //    verwendet, wo Bedeutung und Typ unverändert bleiben.
            // =====================================================================================
            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "Steps",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "Steps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryDestinationId",
                table: "Steps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DistanceFromStartMeters",
                table: "Steps",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "LoadUnitsAfter",
                table: "Steps",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "TravelTimeFromStart",
                table: "Steps",
                type: "time(6)",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            // Echte Umbenennung: Bedeutung und Typ bleiben gleich, nur der Name wird eindeutig.
            migrationBuilder.RenameColumn(
                name: "Service",
                table: "Steps",
                newName: "ServiceTime");

            // ---- Bestandsdaten übernehmen -------------------------------------------------------

            // Die kumulierte Fahrzeit stand in Duration – NICHT in WaitingTime.
            migrationBuilder.Sql("UPDATE `Steps` SET `TravelTimeFromStart` = `Duration`;");

            // Die kumulierte Strecke stand in Distance (nullable).
            migrationBuilder.Sql("UPDATE `Steps` SET `DistanceFromStartMeters` = COALESCE(`Distance`, 0);");

            // LoadJson enthielt ein JSON-Array mit stets genau einem Element, etwa "[2.4]".
            // JSON_VALID schützt vor fehlerhaft gespeicherten Zeichenketten.
            migrationBuilder.Sql(@"
UPDATE `Steps`
SET `LoadUnitsAfter` = CAST(JSON_EXTRACT(`LoadJson`, '$[0]') AS DECIMAL(20,6))
WHERE `LoadJson` IS NOT NULL
  AND JSON_VALID(`LoadJson`)
  AND JSON_EXTRACT(`LoadJson`, '$[0]') IS NOT NULL;");

            // Neubelegung der Aufzählung StepType.
            //
            //   vorher: Start=0, Job=1, Pickup=2, Delivery=3, Break=4, End=5
            //   jetzt:  Start=0, Collection=1, Disposal=2, End=3
            //
            // Bewusst eine einzige Anweisung mit CASE: Mehrere aufeinanderfolgende UPDATEs würden
            // kollidieren, weil der Zielwert eines Schrittes der Ausgangswert des nächsten wäre
            // (3 -> 2 gefolgt von 2 -> 1 würde ehemalige Entleerungen zu Sammelhalten machen).
            // Job (1) und Break (4) wurden nie erzeugt und werden auf Collection abgebildet.
            migrationBuilder.Sql(@"
UPDATE `Steps`
SET `StepType` = CASE `StepType`
    WHEN 0 THEN 0
    WHEN 1 THEN 1
    WHEN 2 THEN 1
    WHEN 3 THEN 2
    WHEN 5 THEN 3
    ELSE 1
END;");

            // Reihenfolge nachtragen. Bisher ergab sie sich allein aus der Primärschlüsselfolge –
            // eine Annahme, die eine Datenbank ohne ORDER BY nicht zusichert.
            migrationBuilder.Sql(@"
UPDATE `Steps` AS `s`
JOIN (
    SELECT `Id`, ROW_NUMBER() OVER (PARTITION BY `RouteId` ORDER BY `Id`) - 1 AS `rn`
    FROM `Steps`
) AS `ordered` ON `s`.`Id` = `ordered`.`Id`
SET `s`.`Sequence` = `ordered`.`rn`;");

            // Fachliche Verweise, soweit rekonstruierbar: Sammelhalte werden über die Koordinate
            // dem Sammelpunkt zugeordnet, Entleerungen der Abladestelle. Altbestand ohne
            // Entsprechung bleibt null – das ist zulässig und bedeutet "Stammsatz nicht mehr
            // vorhanden".
            migrationBuilder.Sql(@"
UPDATE `Steps` AS `s`
JOIN `Jobs` AS `j`
  ON ABS(`j`.`Latitude` - `s`.`Latitude`) < 0.000001
 AND ABS(`j`.`Longitude` - `s`.`Longitude`) < 0.000001
SET `s`.`JobId` = `j`.`Id`
WHERE `s`.`StepType` = 1;");

            migrationBuilder.Sql(@"
UPDATE `Steps` AS `s`
JOIN `DeliveryDestinations` AS `d`
  ON ABS(`d`.`Latitude` - `s`.`Latitude`) < 0.000001
 AND ABS(`d`.`Longitude` - `s`.`Longitude`) < 0.000001
SET `s`.`DeliveryDestinationId` = `d`.`Id`
WHERE `s`.`StepType` = 2;");

            // Koordinaten werden verpflichtend; fehlende Werte auf 0 setzen, damit die
            // Typänderung nicht an NULL scheitert.
            migrationBuilder.Sql(@"
UPDATE `Steps`
SET `Latitude` = COALESCE(`Latitude`, 0), `Longitude` = COALESCE(`Longitude`, 0);");

            // ---- Altspalten entfernen -----------------------------------------------------------
            migrationBuilder.DropColumn(name: "Duration", table: "Steps");
            migrationBuilder.DropColumn(name: "Distance", table: "Steps");
            migrationBuilder.DropColumn(name: "LoadJson", table: "Steps");
            migrationBuilder.DropColumn(name: "WaitingTime", table: "Steps");

            // Kennung des abgelösten Optimierers, ersetzt durch JobId/DeliveryDestinationId.
            migrationBuilder.DropColumn(name: "VroomId", table: "Steps");

            migrationBuilder.AlterColumn<double>(
                name: "Latitude",
                table: "Steps",
                type: "double",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "Longitude",
                table: "Steps",
                type: "double",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Steps",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            // Reihenfolge ist hier zwingend: erst den neuen Index anlegen, dann den alten entfernen.
            //
            // InnoDB verlangt für jeden Fremdschlüssel einen Index, dessen führende Spalte die
            // Fremdschlüsselspalte ist. FK_Steps_Routes_RouteId wird bislang von IX_Steps_RouteId
            // getragen. Wird dieser Index zuerst entfernt, bleibt der Fremdschlüssel ohne Index und
            // MySQL bricht ab:
            //
            //     Cannot drop index 'IX_Steps_RouteId': needed in a foreign key constraint
            //
            // Der zusammengesetzte Index (RouteId, Sequence) hat RouteId als führende Spalte und
            // erfüllt die Anforderung des Fremdschlüssels vollständig. Sobald er besteht, ist
            // IX_Steps_RouteId überflüssig und darf entfernt werden.
            migrationBuilder.CreateIndex(
                name: "IX_Steps_RouteId_Sequence",
                table: "Steps",
                columns: new[] { "RouteId", "Sequence" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_Steps_RouteId",
                table: "Steps");

            migrationBuilder.CreateIndex(
                name: "IX_Steps_JobId",
                table: "Steps",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Steps_DeliveryDestinationId",
                table: "Steps",
                column: "DeliveryDestinationId");

            // SetNull, nicht Cascade: Eine gefahrene Route ist ein Nachweis und darf nicht
            // rückwirkend verschwinden, weil ein Container aus den Stammdaten entfernt wurde.
            migrationBuilder.AddForeignKey(
                name: "FK_Steps_Jobs_JobId",
                table: "Steps",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Steps_DeliveryDestinations_DeliveryDestinationId",
                table: "Steps",
                column: "DeliveryDestinationId",
                principalTable: "DeliveryDestinations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // =====================================================================================
            // 6) Abladestellen und Fahrzeuge
            // =====================================================================================
            migrationBuilder.CreateIndex(
                name: "IX_DeliveryDestinations_ExternalReference",
                table: "DeliveryDestinations",
                column: "ExternalReference");

            // Nutzlast und Sammelmenge beschreiben dieselbe physikalische Größe und verwenden nun
            // denselben Zahlentyp. Zuvor war eine Nutzlast von 12,5 Einheiten nicht darstellbar.
            migrationBuilder.AlterColumn<double>(
                name: "Capacity",
                table: "Vehicles",
                type: "double",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            // =====================================================================================
            // 7) Benutzerkonten: Anmeldename wird eindeutig
            //
            //    Die Eindeutigkeit war bisher nur durch eine Vorabprüfung im Controller
            //    abgesichert. Zwei gleichzeitige Anfragen konnten denselben Namen anlegen, und die
            //    Anmeldung nimmt anschließend das erste Treffer-Konto.
            //
            //    Etwaige Doppelnamen im Bestand werden vor dem Anlegen des Index eindeutig
            //    gemacht, damit die Migration nicht abbricht. Die betroffenen Konten sind danach
            //    an ihrem Namenszusatz erkennbar.
            // =====================================================================================
            migrationBuilder.Sql(@"
UPDATE `UserAccounts` AS `u`
JOIN (
    SELECT `Id`, `Username`,
           ROW_NUMBER() OVER (PARTITION BY `Username` ORDER BY `Id`) AS `rn`
    FROM `UserAccounts`
) AS `dupes` ON `u`.`Id` = `dupes`.`Id`
SET `u`.`Username` = CONCAT(LEFT(`u`.`Username`, 40), '_dup', `u`.`Id`)
WHERE `dupes`.`rn` > 1;");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_Username",
                table: "UserAccounts",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Diese Rücknahme stellt ausschließlich das <b>Schema</b> wieder her, nicht die Daten.
        /// Der Inhalt der entfernten Tabellen (<c>Deliveries</c>, <c>Violations</c>, <c>Stats</c>)
        /// und der entfernten Spalten ist nach dem Vorwärtslauf nicht mehr vorhanden. Vor dem
        /// Anwenden dieser Migration in einer Umgebung mit erhaltungswürdigen Daten ist eine
        /// Sicherung anzulegen.
        /// </para>
        /// <para>
        /// Die Rücknahme ist dennoch vollständig umgesetzt, damit die Migration in einer
        /// Entwicklungs- oder Testumgebung sauber zurückgerollt werden kann.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(name: "IX_UserAccounts_Username", table: "UserAccounts");

            migrationBuilder.AlterColumn<long>(
                name: "Capacity",
                table: "Vehicles",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryDestinations_ExternalReference",
                table: "DeliveryDestinations");

            // ---- Halte ------------------------------------------------------------------------
            migrationBuilder.DropForeignKey(name: "FK_Steps_Jobs_JobId", table: "Steps");
            migrationBuilder.DropForeignKey(
                name: "FK_Steps_DeliveryDestinations_DeliveryDestinationId", table: "Steps");

            migrationBuilder.DropIndex(name: "IX_Steps_JobId", table: "Steps");
            migrationBuilder.DropIndex(name: "IX_Steps_DeliveryDestinationId", table: "Steps");

            // Dieselbe Reihenfolge-Bedingung wie im Vorwärtslauf, nur umgekehrt: Der Index, der
            // FK_Steps_Routes_RouteId trägt, muss angelegt sein, bevor der zusammengesetzte Index
            // entfernt wird.
            migrationBuilder.CreateIndex(name: "IX_Steps_RouteId", table: "Steps", column: "RouteId");

            migrationBuilder.DropIndex(name: "IX_Steps_RouteId_Sequence", table: "Steps");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Steps",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(512)",
                oldMaxLength: 512,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<double>(
                name: "Latitude",
                table: "Steps",
                type: "double",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double");

            migrationBuilder.AlterColumn<double>(
                name: "Longitude",
                table: "Steps",
                type: "double",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double");

            migrationBuilder.AddColumn<uint>(
                name: "VroomId",
                table: "Steps",
                type: "int unsigned",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WaitingTime",
                table: "Steps",
                type: "time(6)",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.AddColumn<string>(
                name: "LoadJson",
                table: "Steps",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Distance",
                table: "Steps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "Duration",
                table: "Steps",
                type: "time(6)",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            // Rückübernahme der Daten, soweit die Spalten es zulassen.
            migrationBuilder.Sql("UPDATE `Steps` SET `Duration` = `TravelTimeFromStart`;");
            migrationBuilder.Sql("UPDATE `Steps` SET `Distance` = `DistanceFromStartMeters`;");
            migrationBuilder.Sql(
                "UPDATE `Steps` SET `LoadJson` = CONCAT('[', `LoadUnitsAfter`, ']');");

            // Umkehrung der Aufzählungs-Neubelegung: Start=0, Collection=1, Disposal=2, End=3
            // zurück auf Start=0, Pickup=2, Delivery=3, End=5.
            migrationBuilder.Sql(@"
UPDATE `Steps`
SET `StepType` = CASE `StepType`
    WHEN 0 THEN 0
    WHEN 1 THEN 2
    WHEN 2 THEN 3
    WHEN 3 THEN 5
    ELSE 2
END;");

            migrationBuilder.RenameColumn(
                name: "ServiceTime",
                table: "Steps",
                newName: "Service");

            migrationBuilder.DropColumn(name: "TravelTimeFromStart", table: "Steps");
            migrationBuilder.DropColumn(name: "LoadUnitsAfter", table: "Steps");
            migrationBuilder.DropColumn(name: "DistanceFromStartMeters", table: "Steps");
            migrationBuilder.DropColumn(name: "DeliveryDestinationId", table: "Steps");
            migrationBuilder.DropColumn(name: "JobId", table: "Steps");
            migrationBuilder.DropColumn(name: "Sequence", table: "Steps");

            // ---- Routen -----------------------------------------------------------------------
            migrationBuilder.DropIndex(name: "IX_Routes_StartDate_EndDate", table: "Routes");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WaitingTime",
                table: "Routes",
                type: "time(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobsOrderIdList",
                table: "Routes",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // ---- Sammelpunkte -----------------------------------------------------------------
            migrationBuilder.DropIndex(name: "IX_Jobs_ExternalReference", table: "Jobs");
            migrationBuilder.DropForeignKey(name: "FK_Jobs_Skills_SkillId", table: "Jobs");

            // Sammelpunkte ohne Fraktion müssen einen Wert erhalten, bevor die Spalte wieder
            // verpflichtend wird.
            migrationBuilder.Sql("UPDATE `Jobs` SET `SkillId` = 0 WHERE `SkillId` IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "SkillId",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Skills_SkillId",
                table: "Jobs",
                column: "SkillId",
                principalTable: "Skills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddColumn<bool>(
                name: "AllowManualAssignment",
                table: "Jobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // ---- Fraktion <-> Abladestelle zurück auf 1:n -------------------------------------
            migrationBuilder.AddColumn<int>(
                name: "DeliveryDestinationId",
                table: "Skills",
                type: "int",
                nullable: true);

            // Bei mehreren Zuordnungen kann nur eine erhalten werden – die Einschränkung, die
            // diese Migration gerade behebt.
            migrationBuilder.Sql(@"
UPDATE `Skills` AS `s`
JOIN (
    SELECT `SkillsId`, MIN(`DeliveryDestinationsId`) AS `DestinationId`
    FROM `DeliveryDestinationSkill`
    GROUP BY `SkillsId`
) AS `link` ON `s`.`Id` = `link`.`SkillsId`
SET `s`.`DeliveryDestinationId` = `link`.`DestinationId`;");

            migrationBuilder.DropTable(name: "DeliveryDestinationSkill");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_DeliveryDestinationId",
                table: "Skills",
                column: "DeliveryDestinationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_DeliveryDestinations_DeliveryDestinationId",
                table: "Skills",
                column: "DeliveryDestinationId",
                principalTable: "DeliveryDestinations",
                principalColumn: "Id");

            // ---- Wiederhergestellte tote Tabellen (leer) --------------------------------------
            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Latitude = table.Column<double>(type: "double", nullable: false),
                    Longitude = table.Column<double>(type: "double", nullable: false),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table => table.PrimaryKey("PK_Deliveries", x => x.Id))
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Stats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ChangeText = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ChangeType = table.Column<int>(type: "int", nullable: false),
                    ChangeTypeValence = table.Column<int>(type: "int", nullable: false),
                    PreviousStat = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Stat = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table => table.PrimaryKey("PK_Stats", x => x.Id))
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Violations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    StepId = table.Column<int>(type: "int", nullable: false),
                    Cause = table.Column<int>(type: "int", nullable: false),
                    Duration = table.Column<double>(type: "double", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Violations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Violations_Steps_StepId",
                        column: x => x.StepId,
                        principalTable: "Steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Violations_StepId",
                table: "Violations",
                column: "StepId");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryDestinationId",
                table: "Jobs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DeliveryDestinationId",
                table: "Jobs",
                column: "DeliveryDestinationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs",
                column: "DeliveryDestinationId",
                principalTable: "Deliveries",
                principalColumn: "Id");
        }
    }
}
