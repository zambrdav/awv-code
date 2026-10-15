using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class M_N_Skill_Route_FreqRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Skills_FrequencyRoutes_FrequencyRouteId",
                table: "Skills");

            migrationBuilder.DropForeignKey(
                name: "FK_Skills_Routes_RouteId",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_Skills_FrequencyRouteId",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_Skills_RouteId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "FrequencyRouteId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "RouteId",
                table: "Skills");

            migrationBuilder.CreateTable(
                name: "FrequencyRouteSkill",
                columns: table => new
                {
                    FrequencyRoutesId = table.Column<int>(type: "int", nullable: false),
                    SkillsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrequencyRouteSkill", x => new { x.FrequencyRoutesId, x.SkillsId });
                    table.ForeignKey(
                        name: "FK_FrequencyRouteSkill_FrequencyRoutes_FrequencyRoutesId",
                        column: x => x.FrequencyRoutesId,
                        principalTable: "FrequencyRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FrequencyRouteSkill_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RouteSkill",
                columns: table => new
                {
                    RoutesId = table.Column<int>(type: "int", nullable: false),
                    SkillsId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteSkill", x => new { x.RoutesId, x.SkillsId });
                    table.ForeignKey(
                        name: "FK_RouteSkill_Routes_RoutesId",
                        column: x => x.RoutesId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RouteSkill_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_FrequencyRouteSkill_SkillsId",
                table: "FrequencyRouteSkill",
                column: "SkillsId");

            migrationBuilder.CreateIndex(
                name: "IX_RouteSkill_SkillsId",
                table: "RouteSkill",
                column: "SkillsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FrequencyRouteSkill");

            migrationBuilder.DropTable(
                name: "RouteSkill");

            migrationBuilder.AddColumn<int>(
                name: "FrequencyRouteId",
                table: "Skills",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteId",
                table: "Skills",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_FrequencyRouteId",
                table: "Skills",
                column: "FrequencyRouteId");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_RouteId",
                table: "Skills",
                column: "RouteId");

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_FrequencyRoutes_FrequencyRouteId",
                table: "Skills",
                column: "FrequencyRouteId",
                principalTable: "FrequencyRoutes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_Routes_RouteId",
                table: "Skills",
                column: "RouteId",
                principalTable: "Routes",
                principalColumn: "Id");
        }
    }
}
