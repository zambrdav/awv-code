using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteTimestampsAndSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "Duration",
                table: "Routes",
                type: "time(6)",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "Service",
                table: "Routes",
                type: "time(6)",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WaitingTime",
                table: "Routes",
                type: "time(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Steps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RouteId = table.Column<int>(type: "int", nullable: false),
                    StepType = table.Column<int>(type: "int", nullable: false),
                    Arrival = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    Service = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    WaitingTime = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Latitude = table.Column<double>(type: "double", nullable: true),
                    Longitude = table.Column<double>(type: "double", nullable: true),
                    VroomId = table.Column<uint>(type: "int unsigned", nullable: true),
                    LoadJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Distance = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Steps_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
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
                name: "IX_Steps_RouteId",
                table: "Steps",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_Violations_StepId",
                table: "Violations",
                column: "StepId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Violations");

            migrationBuilder.DropTable(
                name: "Steps");

            migrationBuilder.DropColumn(
                name: "Duration",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "WaitingTime",
                table: "Routes");
        }
    }
}
