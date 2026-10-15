using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartRoutingAPI.Migrations
{
    /// <inheritdoc />
    public partial class ChangedTimeStampType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Frequency",
                table: "FrequencyRoutes",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(TimeSpan),
                oldType: "time(6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<TimeSpan>(
                name: "Frequency",
                table: "FrequencyRoutes",
                type: "time(6)",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
