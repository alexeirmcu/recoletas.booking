using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recoletas.Booking.Infrastructure.EfModel.Migrations
{
    /// <inheritdoc />
    public partial class OutboxLeaseFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "OutboxMessages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "OutboxMessages");
        }
    }
}
