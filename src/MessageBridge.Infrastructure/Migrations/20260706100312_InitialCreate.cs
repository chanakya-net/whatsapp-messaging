using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageBridge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_processing_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_metadata = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_processing_history", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_processing_history_created_at",
                table: "message_processing_history",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_message_processing_history_message_id_message_type",
                table: "message_processing_history",
                columns: new[] { "message_id", "message_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_processing_history_status",
                table: "message_processing_history",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_processing_history");
        }
    }
}
