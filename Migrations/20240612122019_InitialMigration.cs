using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations;

/// <inheritdoc />
public partial class InitialMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "Deliveries",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Latitude = table.Column<double>(type: "double", nullable: false),
                Longitude = table.Column<double>(type: "double", nullable: false),
                Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table => table.PrimaryKey("PK_Deliveries", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "UserAccounts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Username = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Email = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                PasswordHash = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                PasswordSalt = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                LastLogin = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                FirstName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                LastName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                ProfilePictureUrl = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table => table.PrimaryKey("PK_UserAccounts", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "Vehicles",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Capacity = table.Column<long>(type: "bigint", nullable: false),
                StartLatitude = table.Column<double>(type: "double", nullable: false),
                StartLongitude = table.Column<double>(type: "double", nullable: false),
                EndLatitude = table.Column<double>(type: "double", nullable: false),
                EndLongitude = table.Column<double>(type: "double", nullable: false),
                Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table =>
            table.PrimaryKey("PK_Vehicles", x => x.Id))
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "Jobs",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Capacity = table.Column<long>(type: "bigint", nullable: false),
                DeliveryDestinationId = table.Column<int>(type: "int", nullable: false),
                Latitude = table.Column<double>(type: "double", nullable: false),
                Longitude = table.Column<double>(type: "double", nullable: false),
                Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                Description = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                ExternalReference = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Jobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_Jobs_Deliveries_DeliveryDestinationId",
                    column: x => x.DeliveryDestinationId,
                    principalTable: "Deliveries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_Jobs_DeliveryDestinationId",
            table: "Jobs",
            column: "DeliveryDestinationId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Jobs");

        migrationBuilder.DropTable(
            name: "UserAccounts");

        migrationBuilder.DropTable(
            name: "Vehicles");

        migrationBuilder.DropTable(
            name: "Deliveries");
    }
}
