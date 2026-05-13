using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_skill_table : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source_url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    agent_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill", x => x.id);
                    table.ForeignKey(
                        name: "FK_skill_agent_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_agent_id_name",
                table: "skill",
                columns: new[] { "agent_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill");
        }
    }
}
