using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Scheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvitationResends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "InvitationResentAt",
                table: "Participants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvitationResentAt",
                table: "Participants");
        }
    }
}
