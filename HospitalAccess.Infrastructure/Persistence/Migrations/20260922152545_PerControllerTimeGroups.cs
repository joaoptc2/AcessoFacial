using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerControllerTimeGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As grades globais são descartadas e TODA permissão volta à grade 1, que o sistema
            // passa a gravar como 00:00-23:59 nos sete dias ("sem restrição").
            //
            // Este passo NÃO é opcional. O comando AddTimeGroup do protocolo substitui as 64
            // grades de uma vez, e grade sem definição chega ao aparelho como SEMPRE FECHADO.
            // Uma permissão que continuasse apontando para a antiga grade 7 deixaria a pessoa
            // sem conseguir abrir aquela porta na primeira sincronização, sem erro em lugar
            // nenhum — a porta simplesmente não abriria.
            migrationBuilder.Sql(@"UPDATE ""Permissions"" SET ""TimeGroup"" = 1;");

            migrationBuilder.DropTable(
                name: "TimeGroupSegments");

            migrationBuilder.DropTable(
                name: "TimeGroupSchedules");

            migrationBuilder.DropColumn(
                name: "TimeGroup",
                table: "Users");

            migrationBuilder.CreateTable(
                name: "ControllerTimeGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupNumber = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControllerTimeGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControllerTimeGroups_Controllers_ControllerId",
                        column: x => x.ControllerId,
                        principalTable: "Controllers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ControllerTimeGroupSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerTimeGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weekday = table.Column<byte>(type: "smallint", nullable: false),
                    SegmentIndex = table.Column<byte>(type: "smallint", nullable: false),
                    BeginTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControllerTimeGroupSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControllerTimeGroupSegments_ControllerTimeGroups_Controller~",
                        column: x => x.ControllerTimeGroupId,
                        principalTable: "ControllerTimeGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ControllerTimeGroups_ControllerId_ContentHash",
                table: "ControllerTimeGroups",
                columns: new[] { "ControllerId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ControllerTimeGroups_ControllerId_GroupNumber",
                table: "ControllerTimeGroups",
                columns: new[] { "ControllerId", "GroupNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControllerTimeGroupSegments_ControllerTimeGroupId_Weekday_S~",
                table: "ControllerTimeGroupSegments",
                columns: new[] { "ControllerTimeGroupId", "Weekday", "SegmentIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControllerTimeGroupSegments");

            migrationBuilder.DropTable(
                name: "ControllerTimeGroups");

            migrationBuilder.AddColumn<int>(
                name: "TimeGroup",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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
                    BeginTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    SegmentIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Weekday = table.Column<byte>(type: "smallint", nullable: false)
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
    }
}
