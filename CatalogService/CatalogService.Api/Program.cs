using CatalogService.Api.Endpoints;
using CatalogService.Api.Services;
using CatalogService.Application.Behaviors;
using CatalogService.Application.Queries;
using CatalogService.Infrastructure;
using FluentValidation;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<GetAllProductsQuery>());
builder.Services.AddValidatorsFromAssemblyContaining<GetAllProductsQuery>();
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddCatalogInfrastructure(builder.Configuration);

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("cache");
});

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

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

app.MapCatalogEndpoints();
app.MapGrpcService<CatalogGrpcService>();
app.Run();
