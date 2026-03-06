using FluentValidation;
using MediatR;
using PaymentService.Api.Endpoints;
using PaymentService.Application.Behaviors;
using PaymentService.Application.Commands;
using PaymentService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<ProcessPaymentCommand>());
builder.Services.AddValidatorsFromAssemblyContaining<ProcessPaymentCommand>();
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddPaymentInfrastructure(builder.Configuration);

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

app.MapPaymentEndpoints();
app.MapAdminEventEndpoints();
app.Run();
