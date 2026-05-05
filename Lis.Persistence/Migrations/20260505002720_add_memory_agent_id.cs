using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_memory_agent_id : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "agent_id",
                table: "memory",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_agent_id",
                table: "memory",
                column: "agent_id");

            migrationBuilder.AddForeignKey(
                name: "FK_memory_agent_agent_id",
                table: "memory",
                column: "agent_id",
                principalTable: "agent",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_memory_agent_agent_id",
                table: "memory");

            migrationBuilder.DropIndex(
                name: "IX_memory_agent_id",
                table: "memory");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "memory");
        }
    }
}
