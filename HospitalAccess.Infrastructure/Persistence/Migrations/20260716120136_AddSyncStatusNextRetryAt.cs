using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncStatusNextRetryAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAtUtc",
                table: "SyncStatuses",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: NULL em um Failed significa "falha permanente, fora do retry automático".
            // As falhas que já existiam antes da coluna não foram classificadas — ganham uma
            // tentativa imediata (now()), na qual o UserSyncService as reclassifica (backoff
            // transitório ou quarentena permanente). State = 2 é SyncState.Failed.
            migrationBuilder.Sql(
                """UPDATE "SyncStatuses" SET "NextRetryAtUtc" = now() WHERE "State" = 2;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextRetryAtUtc",
                table: "SyncStatuses");
        }
    }
}
