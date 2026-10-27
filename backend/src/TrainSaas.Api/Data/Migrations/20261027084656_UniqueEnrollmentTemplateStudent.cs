using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrainSaas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueEnrollmentTemplateStudent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ClassEnrollments_TemplateId_StudentId",
                table: "ClassEnrollments",
                columns: new[] { "TemplateId", "StudentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClassEnrollments_TemplateId_StudentId",
                table: "ClassEnrollments");
        }
    }
}
