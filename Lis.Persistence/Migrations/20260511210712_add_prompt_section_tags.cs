using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_prompt_section_tags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "prompt_section",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tags",
                table: "prompt_section");
        }
    }
}
