using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkClockHolidayScheduleAlarmPhotoMifare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CardNumber",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastClockSyncAtUtc",
                table: "Controllers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerName = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RawEventCode = table.Column<int>(type: "integer", nullable: false),
                    Cleared = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerName = table.Column<string>(type: "text", nullable: false),
                    UserCode = table.Column<long>(type: "bigint", nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RawEventCode = table.Column<int>(type: "integer", nullable: false),
                    ImageJpg = table.Column<byte[]>(type: "bytea", nullable: false),
                    DownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPhotos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Holidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<byte>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RepeatsYearly = table.Column<bool>(type: "boolean", nullable: false),
                    HolidayType = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimeGroupSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeGroupSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimeGroupSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeGroupScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weekday = table.Column<byte>(type: "smallint", nullable: false),
                    SegmentIndex = table.Column<byte>(type: "smallint", nullable: false),
                    BeginTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeGroupSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeGroupSegments_TimeGroupSchedules_TimeGroupScheduleId",
                        column: x => x.TimeGroupScheduleId,
                        principalTable: "TimeGroupSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_TimestampUtc",
                table: "AlarmEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_Index",
                table: "Holidays",
                column: "Index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeGroupSchedules_GroupNumber",
                table: "TimeGroupSchedules",
                column: "GroupNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeGroupSegments_TimeGroupScheduleId_Weekday_SegmentIndex",
                table: "TimeGroupSegments",
                columns: new[] { "TimeGroupScheduleId", "Weekday", "SegmentIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmEvents");

            migrationBuilder.DropTable(
                name: "EventPhotos");

            migrationBuilder.DropTable(
                name: "Holidays");

            migrationBuilder.DropTable(
                name: "TimeGroupSegments");

            migrationBuilder.DropTable(
                name: "TimeGroupSchedules");

            migrationBuilder.DropColumn(
                name: "CardNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastClockSyncAtUtc",
                table: "Controllers");
        }
    }
}
