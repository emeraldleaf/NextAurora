using CatalogService.Api.Grpc;
using FluentValidation;
using MediatR;
using OrderService.Api.Endpoints;
using OrderService.Api.GrpcClients;
using OrderService.Application.Behaviors;
using OrderService.Application.Commands;
using OrderService.Application.Interfaces;
using OrderService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PlaceOrderCommand>());
builder.Services.AddValidatorsFromAssemblyContaining<PlaceOrderCommand>();
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddOrderInfrastructure(builder.Configuration);

builder.Services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
{
    o.Address = new Uri("https+http://catalog-service");
});
builder.Services.AddScoped<ICatalogClient, GrpcCatalogClient>();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapOrderEndpoints();
app.MapAdminEventEndpoints();
app.Run();
