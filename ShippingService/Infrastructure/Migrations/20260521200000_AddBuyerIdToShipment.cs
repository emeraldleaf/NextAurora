using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShippingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyerIdToShipment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adds the denormalized BuyerId column populated from PaymentCompletedEvent.BuyerId
            // (which itself flows from OrderPlacedEvent → Payment → PaymentCompletedEvent → Shipment).
            // The default value (Guid.Empty) populates any pre-existing rows. Guid.Empty cannot
            // match a real JWT subject claim, so legacy shipments correctly fail the buyer-scope
            // check at the read endpoint until they are re-created through the saga.
            migrationBuilder.AddColumn<Guid>(
                name: "BuyerId",
                table: "Shipments",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyerId",
                table: "Shipments");
        }
    }
}
