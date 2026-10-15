using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class Moved_START_END_Positions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndLatitude",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "EndLongitude",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "StartLatitude",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "StartLongitude",
                table: "Vehicles");

            migrationBuilder.AddColumn<double>(
                name: "EndLatitude",
                table: "Routes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "EndLongitude",
                table: "Routes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLatitude",
                table: "Routes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLongitude",
                table: "Routes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "EndLatitude",
                table: "FrequencyRoutes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "EndLongitude",
                table: "FrequencyRoutes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLatitude",
                table: "FrequencyRoutes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLongitude",
                table: "FrequencyRoutes",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "VehicleId",
                table: "FrequencyRoutes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_FrequencyRoutes_VehicleId",
                table: "FrequencyRoutes",
                column: "VehicleId");

            migrationBuilder.AddForeignKey(
                name: "FK_FrequencyRoutes_Vehicles_VehicleId",
                table: "FrequencyRoutes",
                column: "VehicleId",
                principalTable: "Vehicles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FrequencyRoutes_Vehicles_VehicleId",
                table: "FrequencyRoutes");

            migrationBuilder.DropIndex(
                name: "IX_FrequencyRoutes_VehicleId",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "EndLatitude",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "EndLongitude",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "StartLatitude",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "StartLongitude",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "EndLatitude",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "EndLongitude",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "StartLatitude",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "StartLongitude",
                table: "FrequencyRoutes");

            migrationBuilder.DropColumn(
                name: "VehicleId",
                table: "FrequencyRoutes");

            migrationBuilder.AddColumn<double>(
                name: "EndLatitude",
                table: "Vehicles",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "EndLongitude",
                table: "Vehicles",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLatitude",
                table: "Vehicles",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "StartLongitude",
                table: "Vehicles",
                type: "double",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
