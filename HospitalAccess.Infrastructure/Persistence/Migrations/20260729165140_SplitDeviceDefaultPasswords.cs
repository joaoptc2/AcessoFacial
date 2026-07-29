using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitDeviceDefaultPasswords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DeviceDefaultPassword",
                table: "SystemSettings",
                newName: "DeviceDefaultCommunicationPassword");

            migrationBuilder.AddColumn<string>(
                name: "DeviceDefaultApiPassword",
                table: "SystemSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-0000000000aa"),
                column: "DeviceDefaultApiPassword",
                value: "");

            // A coluna única antiga podia guardar QUALQUER uma das duas senhas — mantê-la como
            // "senha de comunicação" poderia quebrar o protocolo (ex.: se o valor era a do
            // painel, 1409). Zera as duas: os padrões de FÁBRICA embutidos (FFFFFFFF / 1409)
            // passam a valer; quem usa senhas diferentes redefine pela tela de Configurações.
            migrationBuilder.Sql(
                """UPDATE "SystemSettings" SET "DeviceDefaultCommunicationPassword" = '';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceDefaultApiPassword",
                table: "SystemSettings");

            migrationBuilder.RenameColumn(
                name: "DeviceDefaultCommunicationPassword",
                table: "SystemSettings",
                newName: "DeviceDefaultPassword");
        }
    }
}
