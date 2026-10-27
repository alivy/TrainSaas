using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrainSaas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueConsumptionReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Consumptions_ReversesConsumptionId",
                table: "Consumptions",
                column: "ReversesConsumptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Consumptions_ReversesConsumptionId",
                table: "Consumptions");
        }
    }
}
