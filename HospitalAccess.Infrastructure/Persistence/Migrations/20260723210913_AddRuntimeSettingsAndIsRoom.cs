using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeSettingsAndIsRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceDefaultPassword",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantBaseUrl",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantClearService",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HomeAssistantEnabled",
                table: "SystemSettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantToken",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomeAssistantWelcomeService",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WelcomeBaseImagePath",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WelcomeFontColorHex",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<float>(
                name: "WelcomeFontSize",
                table: "SystemSettings",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WelcomePublicBaseUrl",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "WelcomeTextY",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRoom",
                table: "Controllers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                columns: new[] { "DeviceDefaultPassword", "HomeAssistantBaseUrl", "HomeAssistantClearService", "HomeAssistantEnabled", "HomeAssistantToken", "HomeAssistantWelcomeService", "WelcomeBaseImagePath", "WelcomeFontColorHex", "WelcomeFontSize", "WelcomePublicBaseUrl", "WelcomeTextY" },
                values: new object[] { "", "", "", null, "", "", "", "", null, "", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceDefaultPassword",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantBaseUrl",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantClearService",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantEnabled",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantToken",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "HomeAssistantWelcomeService",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeBaseImagePath",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeFontColorHex",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeFontSize",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "WelcomePublicBaseUrl",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeTextY",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "IsRoom",
                table: "Controllers");
        }
    }
}
