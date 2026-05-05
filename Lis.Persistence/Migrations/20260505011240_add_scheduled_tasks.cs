using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_scheduled_tasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_task",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    cron_expression = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    timezone = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    chat_id = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    channel = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    agent_id = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_task", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_task_enabled",
                table: "scheduled_task",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_task_next_run_at",
                table: "scheduled_task",
                column: "next_run_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_task");
        }
    }
}
