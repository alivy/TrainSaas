using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrainSaas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueSessionTemplateStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_TemplateId_StartAt",
                table: "ClassSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_TemplateId_StartAt",
                table: "ClassSessions",
                columns: new[] { "TemplateId", "StartAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_TemplateId_StartAt",
                table: "ClassSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_TemplateId_StartAt",
                table: "ClassSessions",
                columns: new[] { "TemplateId", "StartAt" });
        }
    }
}
