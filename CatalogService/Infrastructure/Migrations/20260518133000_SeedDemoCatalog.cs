using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CatalogService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedDemoCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), "Phones, laptops, headphones, accessories", "Electronics" },
                    { new Guid("22222222-2222-2222-2222-222222222222"), "Furniture, decor, appliances", "Home & Kitchen" },
                    { new Guid("33333333-3333-3333-3333-333333333333"), "Fiction, non-fiction, technical", "Books" }
                });

            migrationBuilder.InsertData(
                table: "Products",
                columns: new[] { "Id", "CategoryId", "CreatedAt", "Currency", "Description", "IsAvailable", "Name", "Price", "SellerId", "StockQuantity", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a0000001-0000-0000-0000-000000000001"), new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "Demo laptop for the NextAurora portfolio system. Lightweight, fast, fictional.", true, "NextAurora Laptop 15\"", 1299.00m, "seed-seller-1", 12, null },
                    { new Guid("a0000001-0000-0000-0000-000000000002"), new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "Bluetooth 5.3, 30-hour battery, ANC.", true, "Wireless Headphones Pro", 199.00m, "seed-seller-1", 45, null },
                    { new Guid("a0000001-0000-0000-0000-000000000003"), new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "HDMI 4K, 100W PD, 2x USB-A 3.0, SD/microSD, Ethernet.", false, "USB-C Hub (8-in-1)", 49.00m, "seed-seller-1", 0, null },
                    { new Guid("a0000002-0000-0000-0000-000000000001"), new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "48\" wide, dual motor, memory presets. Stable up to 250lb.", true, "Standing Desk (Electric)", 599.00m, "seed-seller-1", 8, null },
                    { new Guid("a0000002-0000-0000-0000-000000000002"), new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "1L gooseneck, induction-compatible.", true, "Ceramic Pour-Over Kettle", 79.00m, "seed-seller-1", 22, null },
                    { new Guid("a0000003-0000-0000-0000-000000000001"), new Guid("33333333-3333-3333-3333-333333333333"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "Martin Kleppmann. The standard reference for distributed systems.", true, "Designing Data-Intensive Applications", 45.00m, "seed-seller-1", 30, null },
                    { new Guid("a0000003-0000-0000-0000-000000000002"), new Guid("33333333-3333-3333-3333-333333333333"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "Hunt & Thomas. Updated edition.", true, "The Pragmatic Programmer (20th Anniversary)", 38.00m, "seed-seller-1", 15, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000001-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000001-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000001-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000002-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000002-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000003-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("a0000003-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"));
        }
    }
}
