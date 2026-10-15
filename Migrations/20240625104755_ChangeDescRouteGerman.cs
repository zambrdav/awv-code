using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations;

/// <inheritdoc />
public partial class ChangeDescRouteGerman : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "Rights",
            table: "UserAccounts",
            newName: "ApplicationRights");

        migrationBuilder.AddColumn<int>(
            name: "AdministrativeRights",
            table: "UserAccounts",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AdministrativeRights",
            table: "UserAccounts");

        migrationBuilder.RenameColumn(
            name: "ApplicationRights",
            table: "UserAccounts",
            newName: "Rights");
    }
}
