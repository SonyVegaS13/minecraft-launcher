using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solaris.Id.Api.Migrations
{
    /// <inheritdoc />
    public partial class LegacyImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegacyImports",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VanillaSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ModdedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ImportedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyImports", x => new { x.UserId, x.SourceId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegacyImports");
        }
    }
}
