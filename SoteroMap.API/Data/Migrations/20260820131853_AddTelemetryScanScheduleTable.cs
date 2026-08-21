using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoteroMap.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryScanScheduleTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryScanSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryScanSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryScanSchedules_DeletedAtUtc",
                table: "TelemetryScanSchedules",
                column: "DeletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryScanSchedules_IsEnabled",
                table: "TelemetryScanSchedules",
                column: "IsEnabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryScanSchedules");
        }
    }
}
