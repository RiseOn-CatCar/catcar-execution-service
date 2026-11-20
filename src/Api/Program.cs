using System.Text;
using CatCar.Contexts.CatalogInventory;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using CatCar.Contexts.CatalogInventory.Features.Execution;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("execution")
    ?? throw new InvalidOperationException("ConnectionStrings:execution is required.");
var messaging = builder.Configuration.GetConnectionString("messaging")
    ?? throw new InvalidOperationException("ConnectionStrings:messaging is required.");

builder.AddServiceDefaults();
builder.Services.AddCatalogInventory(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddNpgSql(database, name: "postgres", tags: ["ready"]);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "CatCar",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "CatCar.AdminApi",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"] ?? string.Empty)),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
});
builder.Services.AddAuthorization();
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(Marker).Assembly);
    options.CodeGeneration.AlwaysUseServiceLocationFor<CatalogInventoryDbContext>();
    options.PersistMessagesWithPostgresql(database);
    options.UseEntityFrameworkCoreTransactions();
    options.OnException<DbUpdateConcurrencyException>()
        .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1));
    options.UseRabbitMq(new Uri(messaging)).AutoProvision().UseConventionalRouting();
    options.Policies.UseDurableInboxOnAllListeners();
    options.Policies.UseDurableOutboxOnAllSendingEndpoints();
});

var app = builder.Build();
var migrationOnly = args.Contains("--migrate", StringComparer.Ordinal);
if (app.Environment.IsDevelopment() || migrationOnly)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<CatalogInventoryDbContext>().Database.MigrateAsync().ConfigureAwait(false);
}

if (migrationOnly)
{
    return;
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapGroup("/api/v1/catalog-inventory").MapCatalogInventoryEndpoints();
app.MapExecutionEndpoints();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.MapDefaultEndpoints();
app.Run();

public partial class Program;
