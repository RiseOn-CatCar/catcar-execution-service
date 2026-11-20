[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace CatCar.Contexts.CatalogInventory.Tests.Integration;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Testcontainers.PostgreSql;

using Xunit;

public sealed class ApiIntegrationFixture : IAsyncLifetime
{
    private const string Secret = "execution-integration-tests-signing-secret-1234567890";
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;
    public string ConnectionString => _database.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        var options = new DbContextOptionsBuilder<CatalogInventoryDbContext>().UseNpgsql(ConnectionString).Options;
        await using (var db = new CatalogInventoryDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings__execution"] = ConnectionString,
            ["ConnectionStrings__messaging"] = "amqp://guest:guest@localhost:5672",
            ["Jwt__Secret"] = Secret,
            ["Jwt__Issuer"] = "CatCar",
            ["Jwt__Audience"] = "CatCar.AdminApi"
        };
        var previous = settings.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
        try
        {
            foreach (var setting in settings)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports()));
            Client = _factory.CreateClient();
        }
        finally
        {
            foreach (var setting in previous)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
        var token = new JwtSecurityToken("CatCar", "CatCar.AdminApi",
            [new Claim(ClaimTypes.NameIdentifier, "test-admin"), new Claim(ClaimTypes.Role, "Administrador")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256));
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _database.DisposeAsync();
    }
}
