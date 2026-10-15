using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class GeometryJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Geometry",
                table: "Routes",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Geometry",
                table: "Routes");
        }
    }
}
