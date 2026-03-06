using CatalogService.Api.Endpoints;
using CatalogService.Api.Services;
using CatalogService.Application.Commands;
using CatalogService.Infrastructure;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(CreateProductCommand).Assembly);
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddValidatorsFromAssemblyContaining<CreateProductCommand>();
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
