using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTvPanelFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TvIpAddress",
                table: "Controllers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "TvLastSeenUtc",
                table: "Controllers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TvPort",
                table: "Controllers",
                type: "integer",
                nullable: false,
                defaultValue: 5555);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TvIpAddress",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "TvLastSeenUtc",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "TvPort",
                table: "Controllers");
        }
    }
}
