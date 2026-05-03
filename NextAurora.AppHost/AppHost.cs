var builder = DistributedApplication.CreateBuilder(args);

// --- Infrastructure ---
var catalogDb = builder.AddPostgres("catalog-pg")
    .AddDatabase("catalog-db");

var ordersDb = builder.AddSqlServer("orders-sql")
    .AddDatabase("orders-db");

var paymentsDb = builder.AddSqlServer("payments-sql")
    .AddDatabase("payments-db");

var shippingDb = builder.AddPostgres("shipping-pg")
    .AddDatabase("shipping-db");

var redis = builder.AddRedis("cache");

var serviceBus = builder.AddAzureServiceBus("messaging");

var orderEventsTopic = serviceBus.AddServiceBusTopic("order-events");
orderEventsTopic.AddServiceBusSubscription("payment-sub");
orderEventsTopic.AddServiceBusSubscription("notify-sub");

var paymentEventsTopic = serviceBus.AddServiceBusTopic("payment-events");
paymentEventsTopic.AddServiceBusSubscription("order-sub");
paymentEventsTopic.AddServiceBusSubscription("shipping-sub");

var shippingEventsTopic = serviceBus.AddServiceBusTopic("shipping-events");
shippingEventsTopic.AddServiceBusSubscription("order-sub");
shippingEventsTopic.AddServiceBusSubscription("notify-sub");

serviceBus.AddServiceBusQueue("send-notification");

var appInsights = builder.AddAzureApplicationInsights("insights");

var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithImport("./realms/nextaurora-realm.json");

var realm = keycloak.AddRealm("nextaurora-realm", "nextaurora");

const string keycloakConfigPrefix = "Keycloak";

// --- Services ---
var catalogService = builder.AddProject<Projects.CatalogService_Api>("catalog-service")
    .WithReference(catalogDb)
    .WithReference(redis)
    .WithReference(appInsights)
    .WithReference(realm, configurationPrefix: keycloakConfigPrefix);

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

builder.AddProject<Projects.NotificationService_Api>("notification-service")
    .WithReference(serviceBus)
    .WithReference(appInsights);

// --- Frontend ---
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
