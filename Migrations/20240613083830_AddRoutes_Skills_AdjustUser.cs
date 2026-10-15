using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations;

/// <inheritdoc />
public partial class AddRoutes_Skills_AdjustUser : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PasswordSalt",
            table: "UserAccounts");

        migrationBuilder.AddColumn<int>(
            name: "RouteId",
            table: "Jobs",
            type: "int",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "FrequencyRoutes",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                Frequency = table.Column<TimeSpan>(type: "time(6)", nullable: false)
            },
            constraints: table =>
            table.PrimaryKey("PK_FrequencyRoutes", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "Routes",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                VehicleId = table.Column<int>(type: "int", nullable: false),
                Color = table.Column<uint>(type: "int unsigned", nullable: false),
                Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Routes", x => x.Id);
                table.ForeignKey(
                    name: "FK_Routes_Vehicles_VehicleId",
                    column: x => x.VehicleId,
                    principalTable: "Vehicles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "Skills",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                FrequencyRouteId = table.Column<int>(type: "int", nullable: true),
                RouteId = table.Column<int>(type: "int", nullable: true),
                VehicleId = table.Column<int>(type: "int", nullable: true),
                Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Skills", x => x.Id);
                table.ForeignKey(
                    name: "FK_Skills_FrequencyRoutes_FrequencyRouteId",
                    column: x => x.FrequencyRouteId,
                    principalTable: "FrequencyRoutes",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_Skills_Routes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "Routes",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_Skills_Vehicles_VehicleId",
                    column: x => x.VehicleId,
                    principalTable: "Vehicles",
                    principalColumn: "Id");
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_Jobs_RouteId",
            table: "Jobs",
            column: "RouteId");

        migrationBuilder.CreateIndex(
            name: "IX_Routes_VehicleId",
            table: "Routes",
            column: "VehicleId");

        migrationBuilder.CreateIndex(
            name: "IX_Skills_FrequencyRouteId",
            table: "Skills",
            column: "FrequencyRouteId");

        migrationBuilder.CreateIndex(
            name: "IX_Skills_RouteId",
            table: "Skills",
            column: "RouteId");

        migrationBuilder.CreateIndex(
            name: "IX_Skills_VehicleId",
            table: "Skills",
            column: "VehicleId");

        migrationBuilder.AddForeignKey(
            name: "FK_Jobs_Routes_RouteId",
            table: "Jobs",
            column: "RouteId",
            principalTable: "Routes",
            principalColumn: "Id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Jobs_Routes_RouteId",
            table: "Jobs");

        migrationBuilder.DropTable(
            name: "Skills");

        migrationBuilder.DropTable(
            name: "FrequencyRoutes");

        migrationBuilder.DropTable(
            name: "Routes");

        migrationBuilder.DropIndex(
            name: "IX_Jobs_RouteId",
            table: "Jobs");

        migrationBuilder.DropColumn(
            name: "RouteId",
            table: "Jobs");

        migrationBuilder.AddColumn<string>(
            name: "PasswordSalt",
            table: "UserAccounts",
            type: "varchar(255)",
            maxLength: 255,
            nullable: false,
            defaultValue: "")
            .Annotation("MySql:CharSet", "utf8mb4");
    }
}
