using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Durable.EfCore.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ParentSessionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ForkEntryId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerTaskId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerSessionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Sequence);
                    table.ForeignKey(
                        name: "FK_events_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "turns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_turns_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_SessionId_Sequence",
                table: "events",
                columns: new[] { "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_events_Type",
                table: "events",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_OwnerTaskId",
                table: "sessions",
                column: "OwnerTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ParentSessionId",
                table: "sessions",
                column: "ParentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_turns_Sequence",
                table: "turns",
                column: "Sequence");

            migrationBuilder.CreateIndex(
                name: "IX_turns_SessionId_Sequence",
                table: "turns",
                columns: new[] { "SessionId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "turns");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
