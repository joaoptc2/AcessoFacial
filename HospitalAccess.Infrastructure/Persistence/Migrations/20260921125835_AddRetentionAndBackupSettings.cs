using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionAndBackupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BackupIntervalHours",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupMaxFiles",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BedStayHistoryRetentionDays",
                table: "SystemSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                columns: new[] { "BackupIntervalHours", "BackupMaxFiles", "BedStayHistoryRetentionDays" },
                values: new object[] { null, null, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupIntervalHours",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "BackupMaxFiles",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "BedStayHistoryRetentionDays",
                table: "SystemSettings");
        }
    }
}
