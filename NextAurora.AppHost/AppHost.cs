using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

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

// Azure Service Bus — Aspire 13 requires explicit `.RunAsEmulator()` for local dev (in 9.x
// the emulator was implicit). Without this, AppHost reports "Missing subscription configuration"
// at startup because Aspire treats the resource as needing a real Azure subscription.
// See CLAUDE.md.
var serviceBus = builder.AddAzureServiceBus("messaging")
    .RunAsEmulator();

// Topic / subscription topology. Each service that publishes events owns a topic; subscribers
// get their own subscription per topic so they can be scaled and dead-lettered independently.
//
// Subscription naming: `{consumer}-{source-events}-sub`. Aspire 13 requires subscription names
// to be globally unique within the bus namespace (not scoped per topic), hence the source
// suffix. The names here must match the
// `ListenToAzureServiceBusSubscription("{sub}", c => c.TopicName = "{topic}")` calls in each
// service's Program.cs — the subscription name and topic are SEPARATE args, NOT a combined
// "{topic}/{sub}" string (that form silently mis-registers the listener). See CLAUDE.md + #148.
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

// Application Insights only when running in Publish mode (i.e. real deploys to Azure).
// Aspire 13 has no local emulator for App Insights — keeping this in for local dev causes
// "Missing subscription configuration" at startup. The Aspire dashboard already provides
// trace/metric/log views for local; OpenTelemetry exports OTLP to Aspire's in-process collector
// regardless. See CLAUDE.md.
IResourceBuilder<AzureApplicationInsightsResource>? appInsights = null;
if (builder.ExecutionContext.IsPublishMode)
{
    appInsights = builder.AddAzureApplicationInsights("insights");
}

// Keycloak runs as a container. The realm definition is imported from a JSON file so the
// dev environment always boots with the same users/clients/roles configured.
var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithImport("./realms/nextaurora-realm.json");

var realm = keycloak.AddRealm("nextaurora-realm", "nextaurora");

// All services read JWT auth config under the `Keycloak` prefix in their configuration —
// see AddDefaultAuthentication() in ServiceDefaults.
const string keycloakConfigPrefix = "Keycloak";

// --- Services: each gets its DB + messaging + telemetry + identity ---
// `WithReference(x)` injects the connection string / endpoint via service discovery.
// `WithReference()` does NOT block startup until the target is healthy — Aspire 13 needs an
// explicit `.WaitFor(x)` for that. Without WaitFor, services boot the moment their config is
// resolvable (which is at app start) and crash trying to reach a still-warming-up dependency.
// Hard rule: every WithReference on a non-trivial dependency (DB, messaging, identity, gRPC
// peer) gets a matching WaitFor. See CLAUDE.md.

// Local helper to apply optional AppInsights reference uniformly.
// In dev (RunMode), appInsights is null and this is a no-op.
static IResourceBuilder<ProjectResource> WithOptionalAppInsights(
    IResourceBuilder<ProjectResource> project,
    IResourceBuilder<AzureApplicationInsightsResource>? insights)
    => insights is null ? project : project.WithReference(insights);

// Origin of the Vite-served React SPA in local dev. Injected only into the services the
// browser calls directly (Catalog, Order, Shipping) — ServiceDefaults registers the CORS
// policy only when this is present. Payment/Notification have no browser-facing surface.
const string SpaDevOrigin = "http://localhost:5173";

var catalogService = WithOptionalAppInsights(
    builder.AddProject<Projects.CatalogService>("catalog-service")
        .WithReference(catalogDb).WaitFor(catalogDb)
        .WithReference(redis).WaitFor(redis), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin);

// Wolverine's AutoProvision() uses the Service Bus *management* API (ServiceBusAdministrationClient)
// to create/verify topics + subscriptions at host startup. The 2.0.0 emulator DOES implement the
// management API (on its 'emulatorhealth' HTTP endpoint, port 5300) — but its SUBSCRIPTION admin
// endpoints return HTTP 500 (topics + queues return 200). So AutoProvision's per-subscription
// SubscriptionExistsAsync/CreateSubscriptionAsync calls fail and the host dies with
// `BrokerInitializationException: Unable to initialize the Broker asb in time`. Locally the topology
// is already declared above (AddServiceBusTopic/AddServiceBusSubscription write the emulator's
// Config.json, which the emulator provisions at container start), and Wolverine's listener attaches
// over AMQP — which works — so provisioning is both impossible AND redundant. (Decompiled proof:
// AzureServiceBusSubscription.InitializeAsync only calls the 500-ing SetupAsync when AutoProvision
// is true; with it false, the listener binds over AMQP with no management call.) Disable it for the
// four Wolverine services in dev; in Publish mode against real Azure, AutoProvision stays on
// (Wolverine:AutoProvision defaults true) to create the entities. Mirrors the test harnesses'
// `Wolverine:AutoProvision=false`. See CLAUDE.md + #148.
const string disableAutoProvision = "Wolverine__AutoProvision";

// OrderService also references catalogService — that gives it the gRPC client config to call
// into Catalog for product validation during order placement.
var orderService = WithOptionalAppInsights(
    builder.AddProject<Projects.OrderService>("order-service")
        .WithReference(ordersDb).WaitFor(ordersDb)
        .WithReference(serviceBus).WaitFor(serviceBus)
        .WithReference(catalogService).WaitFor(catalogService), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin)
    .WithEnvironment(disableAutoProvision, "false");

WithOptionalAppInsights(
    builder.AddProject<Projects.PaymentService>("payment-service")
        .WithReference(paymentsDb).WaitFor(paymentsDb)
        .WithReference(serviceBus).WaitFor(serviceBus), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment(disableAutoProvision, "false");

WithOptionalAppInsights(
    builder.AddProject<Projects.ShippingService>("shipping-service")
        .WithReference(shippingDb).WaitFor(shippingDb)
        .WithReference(serviceBus).WaitFor(serviceBus), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin)
    .WithEnvironment(disableAutoProvision, "false");

// NotificationService is stateless — no DB reference, just messaging + telemetry.
WithOptionalAppInsights(
    builder.AddProject<Projects.NotificationService>("notification-service")
        .WithReference(serviceBus).WaitFor(serviceBus), appInsights)
    .WithEnvironment(disableAutoProvision, "false");

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
