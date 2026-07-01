using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MergeDoorIntoControllerAddUserGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_Doors_DoorId",
                table: "Permissions");

            migrationBuilder.DropTable(
                name: "Doors");

            migrationBuilder.DropColumn(
                name: "DoorName",
                table: "AccessLogs");

            migrationBuilder.RenameColumn(
                name: "DoorId",
                table: "Permissions",
                newName: "ControllerId");

            migrationBuilder.RenameIndex(
                name: "IX_Permissions_DoorId",
                table: "Permissions",
                newName: "IX_Permissions_ControllerId");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelayIndex",
                table: "Controllers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UserGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_GroupId",
                table: "Users",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_Controllers_ControllerId",
                table: "Permissions",
                column: "ControllerId",
                principalTable: "Controllers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_UserGroups_GroupId",
                table: "Users",
                column: "GroupId",
                principalTable: "UserGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_Controllers_ControllerId",
                table: "Permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_UserGroups_GroupId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "UserGroups");

            migrationBuilder.DropIndex(
                name: "IX_Users_GroupId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RelayIndex",
                table: "Controllers");

            migrationBuilder.RenameColumn(
                name: "ControllerId",
                table: "Permissions",
                newName: "DoorId");

            migrationBuilder.RenameIndex(
                name: "IX_Permissions_ControllerId",
                table: "Permissions",
                newName: "IX_Permissions_DoorId");

            migrationBuilder.AddColumn<string>(
                name: "DoorName",
                table: "AccessLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Doors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RelayIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Doors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Doors_Controllers_ControllerId",
                        column: x => x.ControllerId,
                        principalTable: "Controllers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Doors_ControllerId",
                table: "Doors",
                column: "ControllerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_Doors_DoorId",
                table: "Permissions",
                column: "DoorId",
                principalTable: "Doors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
