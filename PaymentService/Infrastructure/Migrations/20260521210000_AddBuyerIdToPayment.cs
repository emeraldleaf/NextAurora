using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyerIdToPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Denormalize BuyerId onto Payment so the recovery sweeper can publish
            // PaymentFailedEvent (which carries BuyerId for downstream NotificationService)
            // without needing the originating request. New rows get BuyerId from the
            // ProcessPaymentCommand; legacy rows (defaultValue = Guid.Empty) are recognized
            // by the sweeper and marked Failed in-DB without an event publish — see
            // PaymentRecoveryJob.
            migrationBuilder.AddColumn<Guid>(
                name: "BuyerId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyerId",
                table: "Payments");
        }
    }
}
