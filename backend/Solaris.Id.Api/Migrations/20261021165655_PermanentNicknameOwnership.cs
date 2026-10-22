using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solaris.Id.Api.Migrations
{
    /// <inheritdoc />
    public partial class PermanentNicknameOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NicknameReservations",
                columns: table => new
                {
                    NormalizedNickname = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ReservedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NicknameReservations", x => x.NormalizedNickname);
                    table.ForeignKey(
                        name: "FK_NicknameReservations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NicknameReservations_UserId",
                table: "NicknameReservations",
                column: "UserId");

            // Retain the current name of every existing Identity account before
            // its first post-upgrade rename. Ownership never moves to a new user.
            migrationBuilder.Sql("""
                INSERT INTO "NicknameReservations" ("NormalizedNickname", "UserId", "ReservedUtc")
                SELECT "NormalizedNickname", "Id", "CreatedUtc" FROM "AspNetUsers";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NicknameReservations");
        }
    }
}
