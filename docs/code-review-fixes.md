# Code Review Fixes

This document describes the 6 issues identified during code review and the fixes applied to address them.

## Issue 1: Payment Idempotency (Critical)

**Problem:** `ProcessPaymentHandler` created a new `Payment` entity on every invocation without checking if one already existed for the given `OrderId`. Duplicate event deliveries or retries would result in multiple payment records and gateway charges for the same order.

**Fix:** Added a `GetByOrderIdAsync()` lookup at the start of the handler. If a payment already exists for the order, the handler returns the existing payment's ID without creating a new one or calling the payment gateway.

**Files changed:**
- `PaymentService/PaymentService.Application/Handlers/ProcessPaymentHandler.cs`

```csharp
var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
if (existing is not null)
    return existing.Id;
```

---

## Issue 2: Stock Race Condition (Critical)

**Problem:** `PlaceOrderHandler` validated stock availability via `ICatalogClient.GetProductAsync()` but never reserved or deducted stock. Between the availability check and order creation, concurrent requests could consume the same stock, leading to overselling.

**Fix:** Added a `ReserveStockAsync` gRPC endpoint to the CatalogService that atomically checks and deducts stock. The `PlaceOrderHandler` now calls this after the availability check. If reservation fails (e.g., another request took the last units), the order is rejected.

**Files changed:**
- `OrderService/OrderService.Application/Interfaces/ICatalogClient.cs` -- added `ReserveStockAsync` method
- `OrderService/OrderService.Application/Handlers/PlaceOrderHandler.cs` -- calls `ReserveStockAsync` after validation
- `OrderService/OrderService.Api/GrpcClients/GrpcCatalogClient.cs` -- gRPC client implementation
- `CatalogService/CatalogService.Api/Protos/catalog.proto` -- added `ReserveStock` RPC definition
- `CatalogService/CatalogService.Application/Commands/ReserveStockCommand.cs` -- new command
- `CatalogService/CatalogService.Application/Handlers/ReserveStockHandler.cs` -- handler that calls `Product.AdjustStock()`
- `CatalogService/CatalogService.Api/Services/CatalogGrpcService.cs` -- gRPC service method

---

## Issue 3: Hardcoded Empty Email and BuyerId (High)

**Problem:** `OrderPlacedNotificationHandler` sent notifications with an empty email string. `ShipmentDispatchedNotificationHandler` used both `Guid.Empty` for the buyer ID and an empty email. Notifications would be created with invalid recipient data.

**Fix:** Introduced an `IRecipientResolver` abstraction in the NotificationService application layer with two methods:
- `ResolveByBuyerIdAsync(Guid buyerId)` -- used by `OrderPlacedNotificationHandler`
- `ResolveByOrderIdAsync(Guid orderId)` -- used by `ShipmentDispatchedNotificationHandler`

Both handlers now resolve recipient information before sending. If resolution fails (returns `null`), the handler exits without sending a notification.

A `StubRecipientResolver` is registered in infrastructure for development. In production, this would be replaced with a real implementation calling an Identity/User service.

**Files changed:**
- `NotificationService/NotificationService.Application/Interfaces/IRecipientResolver.cs` -- new interface + `RecipientInfo` record
- `NotificationService/NotificationService.Infrastructure/Services/StubRecipientResolver.cs` -- stub implementation
- `NotificationService/NotificationService.Infrastructure/DependencyInjection.cs` -- DI registration
- `NotificationService/NotificationService.Application/EventHandlers/OrderPlacedNotificationHandler.cs` -- uses `IRecipientResolver`
- `NotificationService/NotificationService.Application/EventHandlers/ShipmentDispatchedNotificationHandler.cs` -- uses `IRecipientResolver`

---

## Issue 4: Non-Idempotent Event Handlers (High)

**Problem:** `PaymentCompletedHandler` and `ShipmentDispatchedHandler` in the OrderService called `order.MarkAsPaid()` and `order.MarkAsShipped()` without checking the order's current status. These domain methods throw `InvalidOperationException` if the order is not in the expected state. Reprocessing the same event (common in message-based systems) would cause unhandled exceptions.

**Fix:** Added status guards before calling the state transition methods. If the order is already in the target state (or beyond), the handler returns without modifying the order.

**Files changed:**
- `OrderService/OrderService.Application/EventHandlers/PaymentCompletedHandler.cs`
- `OrderService/OrderService.Application/EventHandlers/ShipmentDispatchedHandler.cs`

```csharp
// PaymentCompletedHandler
if (order.Status != OrderStatus.Placed) return;

// ShipmentDispatchedHandler
if (order.Status != OrderStatus.Paid) return;
```

---

## Issue 5: Duplicate Shipments (Medium)

**Problem:** `CreateShipmentHandler` created a new `Shipment` on every invocation without checking if one already existed for the given `OrderId`. If the `PaymentCompletedHandler` in the ShippingService processed the same event twice, duplicate shipments would be created and dispatched.

**Fix:** Added a `GetByOrderIdAsync()` lookup at the start of the handler. If a shipment already exists for the order, the handler returns the existing shipment's ID.

**Files changed:**
- `ShippingService/ShippingService.Application/Handlers/CreateShipmentHandler.cs`

```csharp
var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
if (existing is not null)
    return existing.Id;
```

---

## Issue 6: Missing Domain Validation and State Guards (Bug)

**Problem:** Three domain entities had no validation in their factory methods and no state guards on their transition methods:

- **`Refund`**: Accepted negative/zero amounts, empty `PaymentId`, empty reason. `MarkAsProcessed()` and `MarkAsFailed()` could be called multiple times.
- **`Shipment`**: Accepted empty `OrderId`, empty carrier. `Dispatch()`, `MarkInTransit()`, and `MarkDelivered()` had no state guards, allowing invalid transitions (e.g., delivering without dispatching).
- **`NotificationRequest`**: Accepted empty `RecipientId`, empty email, empty subject, empty body. `MarkAsSent()` and `MarkAsFailed()` could be called multiple times.

**Fix:** Added parameter validation to all three factory methods following the patterns established in `Order.cs` and `Payment.cs`. Added state guards to all transition methods that throw `InvalidOperationException` when the entity is not in the expected status.

**Files changed:**
- `PaymentService/PaymentService.Domain/Entities/Refund.cs`
- `ShippingService/ShippingService.Domain/Entities/Shipment.cs`
- `NotificationService/NotificationService.Domain/Entities/NotificationRequest.cs`

### Refund validation added:
```csharp
if (paymentId == Guid.Empty)
    throw new ArgumentException("Payment ID must not be empty.", nameof(paymentId));
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
ArgumentException.ThrowIfNullOrWhiteSpace(reason);
```

### Shipment state guards added:
```csharp
// Dispatch() -- must be Created
// MarkInTransit() -- must be Dispatched
// MarkDelivered() -- must be InTransit
```

### NotificationRequest validation added:
```csharp
if (recipientId == Guid.Empty)
    throw new ArgumentException("Recipient ID must not be empty.", nameof(recipientId));
ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
ArgumentException.ThrowIfNullOrWhiteSpace(channel);
ArgumentException.ThrowIfNullOrWhiteSpace(subject);
ArgumentException.ThrowIfNullOrWhiteSpace(body);
```

---

## Test Impact

All fixes are covered by unit tests. Previously-skipped tests documenting these bugs now pass:

| Before | After |
|--------|-------|
| 88 passing, 15 skipped | 108 passing, 0 skipped |

New tests added:
- `Handle_WhenPaymentExistsForOrder_ReturnsExistingPaymentId` (ProcessPaymentHandler idempotency)
- `Handle_WhenShipmentExistsForOrder_ReturnsExistingShipmentId` (CreateShipmentHandler idempotency)
- `Handle_WhenOrderAlreadyPaid_IsIdempotent` (PaymentCompletedHandler)
- `Handle_WhenOrderNotPaid_IsIdempotent` (ShipmentDispatchedHandler)
- `Handle_ReservesStockViaCatalogClient` (PlaceOrderHandler stock reservation)
- `Handle_WhenStockReservationFails_ThrowsInvalidOperationException` (reservation failure)
- `Handle_WhenRecipientNotResolved_DoesNotSendNotification` (both notification handlers)
