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

// --- Messaging broker (config-selectable: Messaging:Transport = rabbitmq | azureservicebus) ---
// RabbitMQ is the DEFAULT: it's the deployed (Hetzner) broker, runs cleanly as a single container
// with a management UI, and — unlike the ASB emulator — works end-to-end locally. Azure Service
// Bus is opt-in; its real target is Azure (the local emulator has known limitations, see #148).
// Wolverine abstracts the broker, so each service's Program.cs branches on the same Messaging:
// Transport flag. The connection string is exposed under "messaging" for whichever broker wins.
// See CLAUDE.md + docs/full-saga-deployment-plan.md (D3).
var messagingTransport = builder.Configuration["Messaging:Transport"] ?? "rabbitmq";
var useAzureServiceBus = string.Equals(messagingTransport, "azureservicebus", StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> messaging;
if (useAzureServiceBus)
{
    // Azure Service Bus emulator (Aspire 13 needs explicit .RunAsEmulator()). Topic/subscription
    // topology declared here; subscription naming `{consumer}-{source-events}-sub` (globally unique
    // within the namespace). Must match each service's ListenToAzureServiceBusSubscription("{sub}",
    // c => c.TopicName = "{topic}") — separate args, NOT a combined "{topic}/{sub}" string. See #148.
    var serviceBus = builder.AddAzureServiceBus("messaging").RunAsEmulator();

    var orderEventsTopic = serviceBus.AddServiceBusTopic("order-events");
    orderEventsTopic.AddServiceBusSubscription("payment-orders-sub");      // PaymentService consumes
    orderEventsTopic.AddServiceBusSubscription("notify-orders-sub");       // NotificationService consumes

    var paymentEventsTopic = serviceBus.AddServiceBusTopic("payment-events");
    paymentEventsTopic.AddServiceBusSubscription("order-payments-sub");    // OrderService consumes
    paymentEventsTopic.AddServiceBusSubscription("shipping-payments-sub"); // ShippingService consumes
    paymentEventsTopic.AddServiceBusSubscription("notify-payments-sub");   // NotificationService consumes

    var shippingEventsTopic = serviceBus.AddServiceBusTopic("shipping-events");
    shippingEventsTopic.AddServiceBusSubscription("order-shipping-sub");   // OrderService consumes
    shippingEventsTopic.AddServiceBusSubscription("notify-shipping-sub");  // NotificationService consumes

    serviceBus.AddServiceBusQueue("send-notification");                    // direct command queue

    messaging = serviceBus;
}
else
{
    // RabbitMQ: one container + the management UI (a demo artifact at :15672). Wolverine declares
    // the exchanges/queues/bindings itself via AutoProvision against the live broker, so no
    // hand-declared topology is needed here (contrast the ASB branch above).
    messaging = builder.AddRabbitMQ("messaging").WithManagementPlugin();
}

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

// Wires a Wolverine service to the selected broker: connection-string reference + WaitFor (Aspire 13
// needs an explicit WaitFor for health) + the Messaging:Transport flag so the service picks the
// matching transport branch in its Program.cs. For the ASB *emulator* only, also disable Wolverine
// AutoProvision — its subscription admin endpoints return HTTP 500 (see #148). RabbitMQ keeps
// AutoProvision on and provisions its exchanges/queues against the live broker. See CLAUDE.md.
IResourceBuilder<ProjectResource> WithMessaging(IResourceBuilder<ProjectResource> project)
{
    project = project.WithReference(messaging).WaitFor(messaging)
        .WithEnvironment("Messaging__Transport", messagingTransport);
    if (useAzureServiceBus)
    {
        project = project.WithEnvironment("Wolverine__AutoProvision", "false");
    }

    return project;
}

// OrderService also references catalogService — that gives it the gRPC client config to call
// into Catalog for product validation during order placement.
var orderService = WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.OrderService>("order-service")
        .WithReference(ordersDb).WaitFor(ordersDb)
        .WithReference(catalogService).WaitFor(catalogService), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin));

WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.PaymentService>("payment-service")
        .WithReference(paymentsDb).WaitFor(paymentsDb), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm));

WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.ShippingService>("shipping-service")
        .WithReference(shippingDb).WaitFor(shippingDb), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin));

// NotificationService is stateless — no DB reference, just messaging + telemetry.
WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.NotificationService>("notification-service"), appInsights));

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
