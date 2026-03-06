using MediatR;
using ShippingService.Api.Endpoints;
using ShippingService.Application.Behaviors;
using ShippingService.Application.Commands;
using ShippingService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<CreateShipmentCommand>());
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddShippingInfrastructure(builder.Configuration);

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

app.MapShippingEndpoints();
app.Run();
