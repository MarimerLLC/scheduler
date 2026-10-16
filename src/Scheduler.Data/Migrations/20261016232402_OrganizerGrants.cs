using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Scheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganizerGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanOrganize",
                table: "Users");

            migrationBuilder.CreateTable(
                name: "OrganizerGrants",
                columns: table => new
                {
                    Pattern = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Note = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    GrantedBy = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    GrantedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizerGrants", x => x.Pattern);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizerGrants");

            migrationBuilder.AddColumn<bool>(
                name: "CanOrganize",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
