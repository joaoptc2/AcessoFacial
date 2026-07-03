using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorSyncHealthRetentionOfflineCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastReachError",
                table: "Controllers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenUtc",
                table: "Controllers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControllerSerialNumber",
                table: "AccessLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecordSerialNumber",
                table: "AccessLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventPhotoRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    AccessLogRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    AlarmLogRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    ControllerAuditRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUsername = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Id", "AccessLogRetentionDays", "AlarmLogRetentionDays", "ControllerAuditRetentionDays", "EventPhotoRetentionDays", "UpdatedAtUtc", "UpdatedByUsername" },
                values: new object[] { new Guid("00000000-0000-0000-0000-0000000000aa"), 0, 0, 0, 90, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null });

            migrationBuilder.CreateIndex(
                name: "IX_AccessLogs_ControllerSerialNumber_RecordSerialNumber",
                table: "AccessLogs",
                columns: new[] { "ControllerSerialNumber", "RecordSerialNumber" },
                unique: true,
                filter: "\"RecordSerialNumber\" IS NOT NULL AND \"ControllerSerialNumber\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccessLogs_ControllerSerialNumber_RecordSerialNumber",
                table: "AccessLogs");

            migrationBuilder.DropColumn(
                name: "LastReachError",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "ControllerSerialNumber",
                table: "AccessLogs");

            migrationBuilder.DropColumn(
                name: "RecordSerialNumber",
                table: "AccessLogs");
        }
    }
}
