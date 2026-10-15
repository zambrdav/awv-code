using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations;

/// <inheritdoc />
public partial class M_N_Correlation_Routes_Jobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Jobs_Routes_RouteId",
            table: "Jobs");

        migrationBuilder.DropIndex(
            name: "IX_Jobs_RouteId",
            table: "Jobs");

        migrationBuilder.DropColumn(
            name: "RouteId",
            table: "Jobs");

        migrationBuilder.CreateTable(
            name: "JobRoute",
            columns: table => new
            {
                JobsId = table.Column<int>(type: "int", nullable: false),
                RoutesId = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobRoute", x => new { x.JobsId, x.RoutesId });
                table.ForeignKey(
                    name: "FK_JobRoute_Jobs_JobsId",
                    column: x => x.JobsId,
                    principalTable: "Jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_JobRoute_Routes_RoutesId",
                    column: x => x.RoutesId,
                    principalTable: "Routes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_JobRoute_RoutesId",
            table: "JobRoute",
            column: "RoutesId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "JobRoute");

        migrationBuilder.AddColumn<int>(
            name: "RouteId",
            table: "Jobs",
            type: "int",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Jobs_RouteId",
            table: "Jobs",
            column: "RouteId");

        migrationBuilder.AddForeignKey(
            name: "FK_Jobs_Routes_RouteId",
            table: "Jobs",
            column: "RouteId",
            principalTable: "Routes",
            principalColumn: "Id");
    }
}
