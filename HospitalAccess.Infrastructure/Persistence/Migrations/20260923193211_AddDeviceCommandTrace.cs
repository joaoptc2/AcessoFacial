using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCommandTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeviceTraceEnabled",
                table: "SystemSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // 7 (e não 0) para bater com o padrão da entidade: um prazo 0 seria espremido para
            // 1 dia pelo piso do RuntimeSettingsProvider e apagaria uma coleta de vários dias.
            migrationBuilder.AddColumn<int>(
                name: "DeviceTraceRetentionDays",
                table: "SystemSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateTable(
                name: "DeviceCommandTraces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerName = table.Column<string>(type: "text", nullable: false),
                    ControllerIp = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    QueueWaitMs = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    QueueDepth = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    TimeoutMs = table.Column<int>(type: "integer", nullable: false),
                    RestartCount = table.Column<int>(type: "integer", nullable: false),
                    UserCode = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCommandTraces", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                columns: new[] { "DeviceTraceEnabled", "DeviceTraceRetentionDays" },
                values: new object[] { false, 7 });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommandTraces_ControllerId_StartedAtUtc",
                table: "DeviceCommandTraces",
                columns: new[] { "ControllerId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommandTraces_StartedAtUtc",
                table: "DeviceCommandTraces",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCommandTraces");

            migrationBuilder.DropColumn(
                name: "DeviceTraceEnabled",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "DeviceTraceRetentionDays",
                table: "SystemSettings");
        }
    }
}
