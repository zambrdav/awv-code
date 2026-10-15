using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations;

/// <inheritdoc />
public partial class AddedColorFrequencyRoutes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<uint>(
            name: "Color",
            table: "FrequencyRoutes",
            type: "int unsigned",
            nullable: false,
            defaultValue: 0u);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Color",
            table: "FrequencyRoutes");
    }
}
