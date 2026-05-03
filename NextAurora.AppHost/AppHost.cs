// .NET Aspire orchestration root for NextAurora. Defines the entire local-development topology:
// every container (Postgres, SQL Server, Redis, Service Bus emulator, Keycloak, App Insights),
// every service project, and every dependency edge between them. When you run this project,
// Aspire spins up the containers, sets per-service connection strings/auth via environment
// variables (the magic behind `WithReference`), boots the services in dependency order, and
// opens the dashboard.
//
// SOLID — composition over configuration: each .WithReference(...) call is an explicit dep edge.
// There's no service-discovery magic guessing what to wire — if Order needs gRPC to Catalog,
// it's stated here. That makes it easy to reason about what runs in any environment.

var builder = DistributedApplication.CreateBuilder(args);

// --- Infrastructure: databases, cache, messaging, identity, telemetry ---
// Polyglot persistence: Postgres for read-heavy services (Catalog, Shipping), SQL Server for
// transaction-heavy financial state (Orders, Payments). Each service owns its DB; no other
// service touches it directly.
var catalogDb = builder.AddPostgres("catalog-pg")
    .AddDatabase("catalog-db");

var ordersDb = builder.AddSqlServer("orders-sql")
    .AddDatabase("orders-db");

var paymentsDb = builder.AddSqlServer("payments-sql")
    .AddDatabase("payments-db");

var shippingDb = builder.AddPostgres("shipping-pg")
    .AddDatabase("shipping-db");

// Redis: currently only Catalog has a cache reference, used for distributed product caching
// once we wire it (architecture.md "Future Considerations").
var redis = builder.AddRedis("cache");

// Azure Service Bus emulator — runs locally, exposes a real-protocol endpoint to the services.
var serviceBus = builder.AddAzureServiceBus("messaging");

// Topic / subscription topology. Each service that publishes events owns a topic; subscribers
// get their own subscription per topic so they can be scaled and dead-lettered independently.
//
// Subscription naming: `{consumer}-{source-events}-sub`. Aspire 13 requires subscription names
// to be globally unique within the bus namespace (not scoped per topic), hence the source
// suffix. The strings here must match the `ListenToAzureServiceBusSubscription("{topic}/{sub}")`
// calls in each service's Program.cs.
var orderEventsTopic = serviceBus.AddServiceBusTopic("order-events");
orderEventsTopic.AddServiceBusSubscription("payment-orders-sub");      // PaymentService consumes
orderEventsTopic.AddServiceBusSubscription("notify-orders-sub");       // NotificationService consumes

var paymentEventsTopic = serviceBus.AddServiceBusTopic("payment-events");
paymentEventsTopic.AddServiceBusSubscription("order-payments-sub");    // OrderService consumes
paymentEventsTopic.AddServiceBusSubscription("shipping-payments-sub"); // ShippingService consumes
paymentEventsTopic.AddServiceBusSubscription("notify-payments-sub");   // NotificationService consumes (failure notifications)

var shippingEventsTopic = serviceBus.AddServiceBusTopic("shipping-events");
shippingEventsTopic.AddServiceBusSubscription("order-shipping-sub");   // OrderService consumes
shippingEventsTopic.AddServiceBusSubscription("notify-shipping-sub");  // NotificationService consumes

// Direct queue (not topic) for "send a notification right now" requests — single consumer,
// fan-in only. NotificationService listens here in addition to the topic subscriptions.
serviceBus.AddServiceBusQueue("send-notification");

var appInsights = builder.AddAzureApplicationInsights("insights");

// Keycloak runs as a container. The realm definition is imported from a JSON file so the
// dev environment always boots with the same users/clients/roles configured.
var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithImport("./realms/nextaurora-realm.json");

var realm = keycloak.AddRealm("nextaurora-realm", "nextaurora");

// All services read JWT auth config under the `Keycloak` prefix in their configuration —
// see AddDefaultAuthentication() in ServiceDefaults.
const string keycloakConfigPrefix = "Keycloak";

// --- Services: each gets its DB + messaging + telemetry + identity ---
// `WithReference(x)` is what wires the connection string / endpoint / config: at runtime, the
// service finds its dependencies via service-discovery + injected env vars, not via a config
// file. This is the entire reason Aspire exists — local dev mirrors production-style discovery.

var catalogService = builder.AddProject<Projects.CatalogService_Api>("catalog-service")
    .WithReference(catalogDb)
    .WithReference(redis)
    .WithReference(appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

// OrderService also references catalogService — that gives it the gRPC client config to call
// into Catalog for product validation during order placement.
var orderService = builder.AddProject<Projects.OrderService_Api>("order-service")
    .WithReference(ordersDb)
    .WithReference(serviceBus)
    .WithReference(catalogService)
    .WithReference(appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

builder.AddProject<Projects.PaymentService_Api>("payment-service")
    .WithReference(paymentsDb)
    .WithReference(serviceBus)
    .WithReference(appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

builder.AddProject<Projects.ShippingService_Api>("shipping-service")
    .WithReference(shippingDb)
    .WithReference(serviceBus)
    .WithReference(appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

// NotificationService is stateless — no DB reference, just messaging + telemetry.
builder.AddProject<Projects.NotificationService_Api>("notification-service")
    .WithReference(serviceBus)
    .WithReference(appInsights);

// --- Frontend ---
// Storefront and SellerPortal reference the API services so service-discovery resolves
// `https+http://catalog-service` etc. at runtime. WithExternalHttpEndpoints() exposes them
// outside the Aspire-internal network.
builder.AddProject<Projects.Storefront>("storefront")
    .WithExternalHttpEndpoints()
    .WithReference(catalogService)
    .WithReference(orderService)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

builder.AddProject<Projects.SellerPortal>("seller-portal")
    .WithExternalHttpEndpoints()
    .WithReference(catalogService)
    .WithReference(orderService)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

builder.Build().Run();
