using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solaris.Id.Api.Migrations
{
    /// <inheritdoc />
    public partial class SolarisIdSessionsAndGameAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail before schema changes if legacy duplicate emails need an
            // operator's explicit resolution. Never merge/delete user records.
            migrationBuilder.Sql("""
                LOCK TABLE "AspNetUsers" IN SHARE ROW EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM "AspNetUsers" WHERE "NormalizedEmail" IS NOT NULL GROUP BY "NormalizedEmail" HAVING COUNT(*) > 1) THEN
                    RAISE EXCEPTION 'Solaris ID migration stopped: duplicate normalized emails exist. Resolve ownership explicitly after backup; no accounts were deleted.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActiveUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RefreshExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MfaVerified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthRefreshTokens_AuthSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SolarisGameTicket",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarisGameTicket", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolarisGameTicket_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolarisGameTicket_AuthSessions_AuthSessionId",
                        column: x => x.AuthSessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SolarisTrustedGameSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    PlayedTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarisTrustedGameSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolarisTrustedGameSession_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolarisTrustedGameSession_AuthSessions_AuthSessionId",
                        column: x => x.AuthSessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SolarisGameConnection",
                columns: table => new
                {
                    ServerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeniedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GameSessionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarisGameConnection", x => new { x.ServerId, x.ConnectionId });
                    table.ForeignKey(
                        name: "FK_SolarisGameConnection_SolarisTrustedGameSession_GameSession~",
                        column: x => x.GameSessionId,
                        principalTable: "SolarisTrustedGameSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthRefreshTokens_SessionId",
                table: "AuthRefreshTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthRefreshTokens_TokenHash",
                table: "AuthRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_AccessTokenHash",
                table: "AuthSessions",
                column: "AccessTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_CreatedUtc",
                table: "AuthSessions",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameConnection_Code",
                table: "SolarisGameConnection",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameConnection_ExpiresUtc",
                table: "SolarisGameConnection",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameConnection_GameSessionId",
                table: "SolarisGameConnection",
                column: "GameSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameTicket_AuthSessionId",
                table: "SolarisGameTicket",
                column: "AuthSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameTicket_ExpiresUtc",
                table: "SolarisGameTicket",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameTicket_TokenHash",
                table: "SolarisGameTicket",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolarisGameTicket_UserId",
                table: "SolarisGameTicket",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisTrustedGameSession_AuthSessionId",
                table: "SolarisTrustedGameSession",
                column: "AuthSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisTrustedGameSession_ServerId_ConnectionId",
                table: "SolarisTrustedGameSession",
                columns: new[] { "ServerId", "ConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolarisTrustedGameSession_UserId",
                table: "SolarisTrustedGameSession",
                column: "UserId",
                unique: true,
                filter: "\"ClosedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SolarisTrustedGameSession_UserId_StartedUtc",
                table: "SolarisTrustedGameSession",
                columns: new[] { "UserId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthRefreshTokens");

            migrationBuilder.DropTable(
                name: "SolarisGameConnection");

            migrationBuilder.DropTable(
                name: "SolarisGameTicket");

            migrationBuilder.DropTable(
                name: "SolarisTrustedGameSession");

            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LastActiveUtc",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
