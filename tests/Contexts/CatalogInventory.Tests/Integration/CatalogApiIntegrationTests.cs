namespace CatCar.Contexts.CatalogInventory.Tests.Integration;

using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Text;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class CatalogApiIntegrationTests(ApiIntegrationFixture fixture) : IClassFixture<ApiIntegrationFixture>
{
    private const string Catalog = "/api/v1/catalog-inventory";

    [Fact]
    public async Task CatalogService_CreateUpdateDeactivateAndRead_ReturnsPersistedValues()
    {
        var client = fixture.Client;
        var name = $"Service {Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync($"{Catalog}/services", new
        {
            Name = name,
            Description = "Initial description",
            EstimatedDurationMinutes = 30,
            Price = 120m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await ReadIdAsync(created);
        Assert.EndsWith($"/services/{id}", created.Headers.Location!.ToString());

        var details = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/services/{id}");
        Assert.Equal(name, details.GetProperty("name").GetString());
        Assert.Equal(120m, details.GetProperty("price").GetDecimal());
        Assert.True(details.GetProperty("isActive").GetBoolean());

        var updated = await client.PutAsJsonAsync($"{Catalog}/services/{id}", new
        {
            Name = name,
            Description = "Updated description",
            EstimatedDurationMinutes = 45,
            Price = 150m,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var afterUpdate = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/services/{id}");
        Assert.Equal("Updated description", afterUpdate.GetProperty("description").GetString());
        Assert.Equal(150m, afterUpdate.GetProperty("price").GetDecimal());
        Assert.Equal(45, afterUpdate.GetProperty("estimatedDurationMinutes").GetInt32());

        var activeList = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/services?onlyActive=true");
        Assert.Contains(activeList.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        var deactivated = await client.PatchAsJsonAsync($"{Catalog}/services/{id}/status", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        activeList = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/services?onlyActive=true");
        Assert.DoesNotContain(activeList.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);

        var internalService = await client.GetFromJsonAsync<JsonElement>($"/internal/v1/catalog/services/{id}");
        Assert.Equal(id, internalService.GetProperty("id").GetGuid());
        Assert.Equal(name, internalService.GetProperty("description").GetString());
        Assert.Equal(150m, internalService.GetProperty("unitPrice").GetDecimal());
        Assert.False(internalService.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Catalog}/services/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/v1/catalog/services/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task InventoryItem_CreateUpdateAdjustStockAndDeactivate_ReturnsPersistedValues()
    {
        var client = fixture.Client;
        var sku = $"SKU-{Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync($"{Catalog}/inventory-items", new
        {
            Sku = sku,
            Name = "Oil filter",
            Description = "Initial description",
            UnitPrice = 25m,
            InitialQuantity = 4,
            MinimumStockThreshold = 2,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await ReadIdAsync(created);
        Assert.EndsWith($"/inventory-items/{id}", created.Headers.Location!.ToString());

        var updated = await client.PutAsJsonAsync($"{Catalog}/inventory-items/{id}", new
        {
            Name = "Updated filter",
            Description = "Updated description",
            UnitPrice = 30m,
            MinimumStockThreshold = 5,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var entry = await client.PostAsJsonAsync($"{Catalog}/inventory-items/{id}/stock-movements", new { MovementType = "Entrada", Quantity = 3, Reason = "Restock" });
        Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
        var exit = await client.PostAsJsonAsync($"{Catalog}/inventory-items/{id}/stock-movements", new { MovementType = "Saida", Quantity = 4, Reason = "Use" });
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        var insufficient = await client.PostAsJsonAsync($"{Catalog}/inventory-items/{id}/stock-movements", new { MovementType = "Saida", Quantity = 100, Reason = "Use" });
        Assert.Equal(HttpStatusCode.BadRequest, insufficient.StatusCode);

        var details = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/inventory-items/{id}");
        Assert.Equal(sku.ToUpperInvariant(), details.GetProperty("sku").GetString());
        Assert.Equal("Updated filter", details.GetProperty("name").GetString());
        Assert.Equal(30m, details.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(3, details.GetProperty("quantityInStock").GetInt32());
        Assert.True(details.GetProperty("isBelowMinimumStock").GetBoolean());
        var belowMinimum = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/inventory-items?onlyBelowMinimumStock=true");
        Assert.Contains(belowMinimum.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);

        var deactivated = await client.PatchAsJsonAsync($"{Catalog}/inventory-items/{id}/status", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        var activeList = await client.GetFromJsonAsync<JsonElement>($"{Catalog}/inventory-items?onlyActive=true");
        Assert.DoesNotContain(activeList.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        var internalPart = await client.GetFromJsonAsync<JsonElement>($"/internal/v1/catalog/parts/{id}");
        Assert.Equal(id, internalPart.GetProperty("id").GetGuid());
        Assert.Equal("Updated filter", internalPart.GetProperty("description").GetString());
        Assert.Equal(30m, internalPart.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(3, internalPart.GetProperty("quantityInStock").GetInt32());
        Assert.False(internalPart.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Catalog}/inventory-items/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/v1/catalog/parts/{Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/catalog-inventory/services")]
    [InlineData("/api/v1/catalog-inventory/inventory-items")]
    public async Task AdministrativeEndpoints_WithInvalidToken_ReturnUnauthorized(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdministrativeMutation_WithNonAdministrativeRole_ReturnsForbidden()
    {
        var secret = fixture.Services.GetRequiredService<IConfiguration>()["Jwt:Secret"]!;
        var token = new JwtSecurityToken(
            "CatCar", "CatCar.AdminApi", [new Claim(ClaimTypes.Role, "Customer")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Catalog}/services");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        request.Content = JsonContent.Create(new { Name = "Service", Description = "Description", EstimatedDurationMinutes = 30, Price = 1m });
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("services")]
    [InlineData("parts")]
    public async Task InternalCatalog_WithInvalidToken_RemainsAccessible(string resource)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/catalog/{resource}/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
