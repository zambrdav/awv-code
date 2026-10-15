using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class ManualPriorityFieldJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowManualAssignment",
                table: "Jobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowManualAssignment",
                table: "Jobs");
        }
    }
}
