using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddControllerHttpApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiBaseUrl",
                table: "Controllers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ApiPassword",
                table: "Controllers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ApiToken",
                table: "Controllers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiTokenUpdatedAtUtc",
                table: "Controllers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiUsername",
                table: "Controllers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiBaseUrl",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "ApiPassword",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "ApiToken",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "ApiTokenUpdatedAtUtc",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "ApiUsername",
                table: "Controllers");
        }
    }
}
