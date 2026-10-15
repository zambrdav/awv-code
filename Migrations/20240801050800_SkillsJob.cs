using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class SkillsJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkillId",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SkillId",
                table: "Jobs",
                column: "SkillId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Skills_SkillId",
                table: "Jobs",
                column: "SkillId",
                principalTable: "Skills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Skills_SkillId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_SkillId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SkillId",
                table: "Jobs");
        }
    }
}
