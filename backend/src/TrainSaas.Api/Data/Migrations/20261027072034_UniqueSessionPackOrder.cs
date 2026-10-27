using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrainSaas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueSessionPackOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SessionPacks_OrderId",
                table: "SessionPacks",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SessionPacks_OrderId",
                table: "SessionPacks");
        }
    }
}
