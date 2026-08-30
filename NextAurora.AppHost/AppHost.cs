using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

// .NET Aspire orchestration root for NextAurora. Defines the entire local-development topology:
// every container (Postgres, SQL Server, Redis, RabbitMQ, Keycloak, App Insights),
// every service project, and every dependency edge between them. When you run this project,
// Aspire spins up the containers, sets per-service connection strings/auth via environment
// variables (the magic behind `WithReference`), boots the services in dependency order, and
// opens the dashboard.
//
// SOLID — composition over configuration: each .WithReference(...) call is an explicit dep edge.
// There's no service-discovery magic guessing what to wire — if Order needs gRPC to Catalog,
// it's stated here. That makes it easy to reason about what runs in any environment.

const string keycloakHostname = "http://localhost:8080/";

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

// Redis: L2 tier of Catalog's HybridCache (see HybridProductCache); only Catalog references it today.
var redis = builder.AddRedis("cache");

// --- Messaging broker: RabbitMQ ---
// One container + the management UI (a demo artifact at :15672). Wolverine declares the
// exchanges/queues/bindings itself via AutoProvision against the live broker, so no topology is
// hand-declared here. RabbitMQ is also the deployed broker (Hetzner) — dev/prod parity. The
// connection string is exposed under "messaging". The transport stays swappable (~5-line Wolverine
// block per service); the previous cloud-broker wiring was evaluated and removed — its emulator
// couldn't run the saga locally and there's no Azure deployment today. See CLAUDE.md + docs/full-saga-deployment-plan.md (D3) + #148.
var messaging = builder.AddRabbitMQ("messaging").WithManagementPlugin();

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
var keycloak = builder.AddKeycloakContainer("keycloak", port: 8080)
    .WithHostname(keycloakHostname)
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
const string SpaDevOrigin = "http://localhost:5173;http://127.0.0.1:5173";

var catalogService = WithOptionalAppInsights(
    builder.AddProject<Projects.CatalogService>("catalog-service")
        .WithReference(catalogDb).WaitFor(catalogDb)
        .WithReference(redis).WaitFor(redis), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin);

// Wires a Wolverine service to RabbitMQ: connection-string reference + WaitFor (Aspire 13 needs an
// explicit WaitFor for health). Wolverine AutoProvisions its exchanges/queues against the live
// broker at startup. See CLAUDE.md.
IResourceBuilder<ProjectResource> WithMessaging(IResourceBuilder<ProjectResource> project)
{
    return project.WithReference(messaging).WaitFor(messaging);
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
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    // Kill-switch demo (#208): browser calls the demo endpoints directly, so Payment needs
    // the SPA CORS origins; DemoMode gates the endpoints on (local dev = always demo).
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin)
    .WithEnvironment("DemoMode", "true"));

WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.ShippingService>("shipping-service")
        .WithReference(shippingDb).WaitFor(shippingDb), appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix).WaitFor(realm)
    .WithEnvironment("Frontend__AllowedOrigins", SpaDevOrigin));

// NotificationService is stateless — no DB reference, just messaging + telemetry.
WithMessaging(WithOptionalAppInsights(
    builder.AddProject<Projects.NotificationService>("notification-service"), appInsights));

// --- Frontend ---
// The customer storefront is the React 19 + Vite SPA in frontend/ — run separately
// (`npm run dev` on :5173, see SpaDevOrigin above); it is not an Aspire resource.
// SellerPortal references the API services so service-discovery resolves
// `https+http://catalog-service` etc. at runtime. WithExternalHttpEndpoints() exposes it
// outside the Aspire-internal network.
builder.AddProject<Projects.SellerPortal>("seller-portal")
    .WithExternalHttpEndpoints()
    .WithHttpEndpoint(port: 5177, targetPort: 5177, isProxied: false)
    .WithReference(catalogService)
    .WithReference(orderService)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

await builder.Build().RunAsync();
