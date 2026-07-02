using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupControllerDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupControllerDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupControllerDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupControllerDefaults_Controllers_ControllerId",
                        column: x => x.ControllerId,
                        principalTable: "Controllers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupControllerDefaults_UserGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupControllerDefaults_ControllerId",
                table: "GroupControllerDefaults",
                column: "ControllerId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupControllerDefaults_GroupId_ControllerId",
                table: "GroupControllerDefaults",
                columns: new[] { "GroupId", "ControllerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupControllerDefaults");
        }
    }
}
