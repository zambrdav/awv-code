using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class M_N_Vehicle_Skills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Skills_Vehicles_VehicleId",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_Skills_VehicleId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "VehicleId",
                table: "Skills");

            migrationBuilder.CreateTable(
                name: "SkillVehicle",
                columns: table => new
                {
                    SkillsId = table.Column<int>(type: "int", nullable: false),
                    VehiclesId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillVehicle", x => new { x.SkillsId, x.VehiclesId });
                    table.ForeignKey(
                        name: "FK_SkillVehicle_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SkillVehicle_Vehicles_VehiclesId",
                        column: x => x.VehiclesId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SkillVehicle_VehiclesId",
                table: "SkillVehicle",
                column: "VehiclesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillVehicle");

            migrationBuilder.AddColumn<int>(
                name: "VehicleId",
                table: "Skills",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_VehicleId",
                table: "Skills",
                column: "VehicleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_Vehicles_VehicleId",
                table: "Skills",
                column: "VehicleId",
                principalTable: "Vehicles",
                principalColumn: "Id");
        }
    }
}
