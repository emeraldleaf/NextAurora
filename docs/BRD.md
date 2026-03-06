# NextAurora - Business Requirements Document (BRD)

## Document Information

| Field | Value |
|-------|-------|
| Project Name | NextAurora |
| Version | 1.0 |
| Status | In Development |
| Last Updated | March 2026 |

---

## 1. Executive Summary

NextAurora is an e-commerce platform that enables customers to browse and purchase products online, and merchants to list and manage their inventory. The platform processes orders end-to-end: from product discovery through payment, shipment, and delivery notification.

The system is built as a set of independently deployable microservices to support team scalability, independent deployment cycles, and technology flexibility.

---

## 2. Business Objectives

1. **Enable online commerce** - Customers can browse products, place orders, and track shipments
2. **Support multiple sellers** - Merchants can list products and manage inventory through a dedicated portal
3. **Automate order fulfillment** - The order-to-delivery pipeline is fully automated with no manual intervention
4. **Ensure reliability** - Services operate independently; failure in one does not cascade to others
5. **Support growth** - Architecture allows horizontal scaling of individual services based on demand

---

## 3. Stakeholders

| Role | Description |
|------|-------------|
| **Customer** | End user who browses products, places orders, and receives shipments |
| **Seller/Merchant** | Business or individual who lists products for sale |
| **Platform Operator** | Team managing infrastructure, monitoring, and operations |

---

## 4. Functional Requirements

### 4.1 Product Catalog

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| CAT-01 | Customers can browse all available products | High | Implemented (API) |
| CAT-02 | Customers can search products by keyword | High | Implemented (API) |
| CAT-03 | Customers can view product details (name, description, price, availability) | High | Implemented (API) |
| CAT-04 | Sellers can create new product listings | High | Implemented (API) |
| CAT-05 | Sellers can update product details and pricing | High | Implemented (API) |
| CAT-06 | Products display real-time stock availability | High | Implemented |
| CAT-07 | Products are organized by categories | Medium | Implemented (Domain) |
| CAT-08 | Product data is cached for fast retrieval | Medium | Infrastructure ready (Redis), not yet utilized |
| CAT-09 | Sellers can upload product images | Low | Not implemented |
| CAT-10 | Product ratings and reviews | Low | Not implemented |

### 4.2 Order Management

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| ORD-01 | Customers can place orders with one or more products | High | Implemented |
| ORD-02 | Product availability and pricing is validated at order time | High | Implemented (gRPC validation) |
| ORD-03 | Insufficient stock prevents order placement | High | Implemented |
| ORD-04 | Server-side pricing is used (not client-submitted prices) | High | Implemented |
| ORD-05 | Customers can view their order history | High | Implemented (API) |
| ORD-06 | Customers can view order details and status | High | Implemented (API) |
| ORD-07 | Order status updates automatically through the fulfillment pipeline | High | Implemented (event-driven) |
| ORD-08 | Customers can cancel orders before shipment | Medium | Not implemented |
| ORD-09 | Order status values: Placed, Paid, Shipped, Delivered, Cancelled | High | Implemented |

### 4.3 Payment Processing

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| PAY-01 | Payment is automatically initiated when an order is placed | High | Implemented |
| PAY-02 | Payment processing integrates with Stripe | High | Implemented (simulated gateway) |
| PAY-03 | Successful payments update order status to Paid | High | Implemented |
| PAY-04 | Failed payments are recorded with failure reason | High | Implemented |
| PAY-05 | Payment supports multiple currencies | Medium | Implemented (currency field) |
| PAY-06 | Refund processing | Medium | Domain model ready, workflow not implemented |
| PAY-07 | Payment retry on transient failures | Low | Not implemented |

### 4.4 Shipping & Tracking

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| SHP-01 | Shipment is automatically created when payment completes | High | Implemented |
| SHP-02 | System assigns a carrier (FedEx, UPS, USPS, DHL) | High | Implemented |
| SHP-03 | System generates a unique tracking number | High | Implemented |
| SHP-04 | Customers can view shipment status and tracking info | High | Implemented (API) |
| SHP-05 | Shipment status values: Created, Dispatched, InTransit, Delivered | High | Implemented |
| SHP-06 | Tracking events record shipment milestones | High | Implemented |
| SHP-07 | Integration with real carrier tracking APIs | Low | Not implemented (simulated) |

### 4.5 Notifications

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| NTF-01 | Customer receives notification when order is placed | High | Implemented (console) |
| NTF-02 | Customer receives notification when order is shipped (with tracking) | High | Implemented (console) |
| NTF-03 | Notifications sent via email | Medium | Not implemented (console sender) |
| NTF-04 | Notifications sent via SMS | Low | Not implemented |
| NTF-05 | Notification preferences per customer | Low | Not implemented |

### 4.6 Storefront (Customer UI)

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| SF-01 | Product listing page with search | High | Not implemented (scaffold only) |
| SF-02 | Product detail page | High | Not implemented |
| SF-03 | Shopping cart | High | Not implemented |
| SF-04 | Checkout flow | High | Not implemented |
| SF-05 | Order history and tracking | Medium | Not implemented |
| SF-06 | User registration and login | Medium | Not implemented |
| SF-07 | Responsive design (mobile-friendly) | Medium | Not implemented |

### 4.7 Seller Portal (Merchant UI)

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| SP-01 | Product management (create, edit, delete) | High | Not implemented (scaffold only) |
| SP-02 | Inventory management (stock levels) | High | Not implemented |
| SP-03 | Sales dashboard | Medium | Not implemented |
| SP-04 | Order fulfillment view | Medium | Not implemented |
| SP-05 | Seller registration and authentication | Medium | Not implemented |

---

## 5. Non-Functional Requirements

### 5.1 Performance

| ID | Requirement | Target |
|----|-------------|--------|
| PERF-01 | API response time for product queries | < 200ms (p95) |
| PERF-02 | Order placement end-to-end | < 500ms |
| PERF-03 | gRPC product validation latency | < 50ms |
| PERF-04 | Event processing latency (Service Bus) | < 2 seconds |

### 5.2 Reliability

| ID | Requirement | Target |
|----|-------------|--------|
| REL-01 | Service availability | 99.9% per service |
| REL-02 | No data loss on service failure | Event-driven with at-least-once delivery |
| REL-03 | Independent service failure isolation | Service Bus decouples services |
| REL-04 | Health check endpoints on all services | Implemented (/health, /alive) |

### 5.3 Scalability

| ID | Requirement | Target |
|----|-------------|--------|
| SCL-01 | Horizontal scaling per service | Supported (stateless services) |
| SCL-02 | Database per service | Implemented (polyglot persistence) |
| SCL-03 | Cache layer for read-heavy services | Redis infrastructure ready |

### 5.4 Security

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| SEC-01 | API authentication (JWT/OAuth2) | High | Not implemented |
| SEC-02 | Service-to-service authentication | High | Not implemented |
| SEC-03 | Input validation on all endpoints | High | Implemented (FluentValidation + Wolverine pipeline + domain guard clauses) |
| SEC-04 | Secrets management | Medium | Aspire User Secrets (dev) |
| SEC-05 | HTTPS enforcement | Medium | Implemented (production redirection) |
| SEC-06 | Global exception handling (no internal state leakage) | High | Implemented (ProblemDetails + trace IDs) |
| SEC-07 | Domain invariant enforcement | High | Implemented (guard clauses in all entity factory methods) |

### 5.5 Observability

| ID | Requirement | Status |
|----|-------------|--------|
| OBS-01 | Distributed tracing across all services | Implemented (OpenTelemetry) |
| OBS-02 | Structured logging with correlation | Implemented (OpenTelemetry) |
| OBS-03 | Runtime and HTTP metrics | Implemented (OpenTelemetry) |
| OBS-04 | gRPC call tracing | Implemented |
| OBS-05 | Health check dashboard | Implemented (Aspire Dashboard) |
| OBS-06 | Production observability backend (Jaeger/Grafana) | Not implemented (OTLP exporter ready) |

### 5.6 Code Quality

| ID | Requirement | Status |
|----|-------------|--------|
| CQ-01 | Static code analysis on build | Implemented (3 analyzers) |
| CQ-02 | Warnings treated as errors | Implemented |
| CQ-03 | Centralized package version management | Implemented (CPM) |
| CQ-04 | Consistent coding standards | Implemented (.editorconfig) |
| CQ-05 | CI/CD pipeline | Implemented (GitHub Actions) |
| CQ-06 | Unit test coverage | Not implemented |
| CQ-07 | Integration test coverage | Not implemented |

---

## 6. Business Process Flows

### 6.1 Order Fulfillment (Happy Path)

```
1. Customer browses products on Storefront
2. Customer adds items to cart and proceeds to checkout
3. Customer submits order
4. OrderService validates products against CatalogService (gRPC)
   - Verifies each product exists
   - Verifies each product is available
   - Verifies sufficient stock for requested quantity
   - Uses server-side pricing (not client-submitted)
5. OrderService creates order (status: Placed)
6. OrderService publishes OrderPlacedEvent
7. PaymentService receives event, processes payment via Stripe
8. PaymentService publishes PaymentCompletedEvent
9. OrderService receives event, updates order status to Paid
10. ShippingService receives event, creates shipment
    - Assigns carrier and tracking number
    - Dispatches shipment
11. ShippingService publishes ShipmentDispatchedEvent
12. OrderService receives event, updates order status to Shipped
13. NotificationService sends:
    - "Order Received" notification (step 6)
    - "Order Shipped" notification with tracking info (step 11)
14. Customer receives notifications and can track shipment
```

### 6.2 Payment Failure

```
1. Steps 1-7 from happy path
2. PaymentService payment fails (gateway rejects)
3. PaymentService publishes PaymentFailedEvent (includes BuyerId)
4. OrderService receives event → marks order status as PaymentFailed
5. NotificationService receives event → sends "Payment Failed" email to buyer
```

### 6.3 Product Management (Seller)

```
1. Seller logs into SellerPortal
2. Seller creates new product listing
   - Name, description, price, currency, category, stock quantity
3. CatalogService stores product (status: available if stock > 0)
4. Product appears in Storefront search results
5. Seller can update product details and adjust stock
```

---

## 7. Data Requirements

### Entities & Ownership

| Entity | Owning Service | Key Fields |
|--------|---------------|------------|
| Product | CatalogService | Name, Price, StockQuantity, IsAvailable |
| Category | CatalogService | Name, Description |
| Order | OrderService | BuyerId, Status, TotalAmount, Lines |
| OrderLine | OrderService | ProductId, ProductName, Quantity, UnitPrice |
| Payment | PaymentService | OrderId, Amount, Status, Provider |
| Refund | PaymentService | PaymentId, Amount, Reason, Status |
| Shipment | ShippingService | OrderId, Carrier, TrackingNumber, Status |
| TrackingEvent | ShippingService | ShipmentId, Description, Status |
| NotificationRequest | NotificationService | RecipientId, Subject, Body, Channel |

### Data Consistency Model

- **Within a service:** Strong consistency (ACID transactions via EF Core)
- **Across services:** Eventual consistency (Azure Service Bus at-least-once delivery)
- **Product validation at order time:** Strong consistency via synchronous gRPC call

---

## 8. Integration Points

| Integration | Type | Status |
|------------|------|--------|
| Stripe (Payment Gateway) | External API | Simulated |
| Email Provider (SendGrid/Twilio) | External API | Console placeholder |
| Carrier APIs (FedEx/UPS/USPS/DHL) | External API | Simulated |
| Azure Service Bus | Managed Service | Implemented (emulator for dev) |
| PostgreSQL | Database | Implemented (Docker) |
| SQL Server | Database | Implemented (Docker) |
| Redis | Cache | Infrastructure ready |
| Azure Application Insights | Observability | Configuration ready |

---

## 9. Assumptions & Constraints

### Assumptions
1. Customers have a stable internet connection for the Blazor WASM storefront
2. Azure Service Bus (or emulator) is available for event processing
3. Payment gateway is reachable for order processing
4. Single currency per order (multi-currency across orders is supported)

### Constraints
1. No real-time inventory reservation (stock checked at order time, not reserved in cart)
2. Event processing is at-least-once (handlers should be idempotent for production)
3. No distributed transactions across services
4. Notification delivery is best-effort (no delivery guarantees without production email provider)

---

## 10. Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Payment service failure | Orders placed but not processed | Service Bus queues messages; payment processes when service recovers |
| Stock oversold (concurrent orders) | Customer dissatisfaction | Future: Implement stock reservation or optimistic concurrency |
| Event message loss | Incomplete order lifecycle | Azure Service Bus provides at-least-once delivery with dead letter queues |
| Service Bus unavailable | Order pipeline halts | Aspire resilience handlers; future: outbox pattern for guaranteed publishing |
| gRPC catalog call failure | Order placement fails | HTTP resilience handler retries; future: circuit breaker with cached fallback |

---

## 11. Success Criteria

| Metric | Target |
|--------|--------|
| Order placement success rate | > 99% |
| Payment processing success rate | > 98% |
| Event processing lag | < 5 seconds end-to-end |
| API availability | > 99.9% per service |
| Zero data loss | No orphaned orders or payments |

---

## 12. Glossary

| Term | Definition |
|------|-----------|
| **Aggregate** | A cluster of domain objects treated as a single unit for data changes |
| **CQRS** | Command Query Responsibility Segregation - separating read and write operations |
| **Choreography Saga** | A distributed transaction pattern where services react to events independently |
| **gRPC** | A high-performance RPC framework using Protocol Buffers |
| **Service Bus** | Azure messaging service for pub/sub and queue-based communication |
| **Eventual Consistency** | Data across services will become consistent over time, not immediately |
| **Dead Letter Queue** | A queue for messages that cannot be processed after multiple attempts |
| **Idempotent** | An operation that produces the same result regardless of how many times it is executed |
