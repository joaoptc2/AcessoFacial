using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBedExtraAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantExtraActionLabel",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantExtraActionService",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                columns: new[] { "HomeAssistantExtraActionLabel", "HomeAssistantExtraActionService" },
                values: new object[] { "", "" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeAssistantExtraActionLabel",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantExtraActionService",
                table: "SystemSettings");
        }
    }
}
