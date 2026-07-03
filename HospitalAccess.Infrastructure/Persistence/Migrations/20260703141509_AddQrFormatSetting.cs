using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQrFormatSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QrFormat",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                column: "QrFormat",
                value: "PlainText");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QrFormat",
                table: "SystemSettings");
        }
    }
}
