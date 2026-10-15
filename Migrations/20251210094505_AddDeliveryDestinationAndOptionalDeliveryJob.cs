using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryDestinationAndOptionalDeliveryJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryDestinationId",
                table: "Skills",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DeliveryDestinationId",
                table: "Jobs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateTable(
                name: "DeliveryDestinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Latitude = table.Column<double>(type: "double", nullable: false),
                    Longitude = table.Column<double>(type: "double", nullable: false),
                    MaximumCapacity = table.Column<double>(type: "double", nullable: false),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryDestinations", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_DeliveryDestinationId",
                table: "Skills",
                column: "DeliveryDestinationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs",
                column: "DeliveryDestinationId",
                principalTable: "Deliveries",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Skills_DeliveryDestinations_DeliveryDestinationId",
                table: "Skills",
                column: "DeliveryDestinationId",
                principalTable: "DeliveryDestinations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Skills_DeliveryDestinations_DeliveryDestinationId",
                table: "Skills");

            migrationBuilder.DropTable(
                name: "DeliveryDestinations");

            migrationBuilder.DropIndex(
                name: "IX_Skills_DeliveryDestinationId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "DeliveryDestinationId",
                table: "Skills");

            migrationBuilder.AlterColumn<int>(
                name: "DeliveryDestinationId",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                table: "Jobs",
                column: "DeliveryDestinationId",
                principalTable: "Deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
