namespace CatCar.Contexts.CatalogInventory.Tests.Integration;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CatCar.Contexts.CatalogInventory.Domain.InventoryItems;
using CatCar.Contexts.CatalogInventory.Domain.InventoryReservations;
using CatCar.Contexts.CatalogInventory.Features.Execution;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ExecutionEndpointsHttpTests(ApiIntegrationFixture fixture) : IClassFixture<ApiIntegrationFixture>
{
    private const string Jobs = "/api/v1/execution/jobs";

    [Fact]
    public async Task ListAndGet_FilterAndOrderQueuedJobs_RequireAuthentication()
    {
        var now = DateTime.UtcNow;
        var earlier = await SeedJobAsync("Queued", now.AddMinutes(-5));
        var later = await SeedJobAsync("Queued", now.AddMinutes(-4));
        var running = await SeedJobAsync("InProgress", now.AddMinutes(-3));
        var reserved = await SeedJobAsync("Reserved", now.AddMinutes(-2));

        using var response = await fixture.Client.GetAsync($"{Jobs}?status=Queued");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.EnumerateArray().Select(x => x.GetProperty("workOrderId").GetGuid()).ToList();
        Assert.True(ids.IndexOf(earlier) >= 0);
        Assert.True(ids.IndexOf(later) > ids.IndexOf(earlier));
        Assert.DoesNotContain(running, ids);
        Assert.DoesNotContain(reserved, ids);

        using var detail = await fixture.Client.GetAsync($"{Jobs}/{earlier}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("Queued", detailBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync($"{Jobs}/{Guid.NewGuid()}")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, Jobs);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var unauthorized = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Start_TransitionsQueuedJobOnce_AndRejectsInvalidStates()
    {
        var id = await SeedJobAsync("Queued", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync($"{Jobs}/{id}/start", null)).StatusCode);
        var first = await LoadJobAsync(id);
        Assert.Equal("InProgress", first.Status);
        Assert.NotNull(first.QueuedAt);
        Assert.NotNull(first.StartedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync($"{Jobs}/{id}/start", null)).StatusCode);
        Assert.Equal(first.StartedAt, (await LoadJobAsync(id)).StartedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsync($"{Jobs}/{Guid.NewGuid()}/start", null)).StatusCode);
        var reserved = await SeedJobAsync("Reserved", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsync($"{Jobs}/{reserved}/start", null)).StatusCode);
        var completed = await SeedJobAsync("Completed", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Client.PostAsync($"{Jobs}/{completed}/start", null)).StatusCode);
    }

    [Fact]
    public async Task Complete_ConsumesReservationOnce_AndRejectsInvalidStates()
    {
        var id = await SeedJobAsync("InProgress", DateTime.UtcNow, withReservation: true);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync($"{Jobs}/{id}/complete", null)).StatusCode);
        var first = await LoadJobAsync(id);
        Assert.Equal("Completed", first.Status);
        Assert.NotNull(first.CompletedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync($"{Jobs}/{id}/complete", null)).StatusCode);
        Assert.Equal(first.CompletedAt, (await LoadJobAsync(id)).CompletedAt);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogInventoryDbContext>();
            var reservation = await db.InventoryReservations.SingleAsync(x => x.WorkOrderId == id);
            Assert.Equal(InventoryReservationStatus.Consumed, reservation.Status);
            Assert.NotNull(reservation.ConsumedAt);
            Assert.Equal(2, (await db.InventoryItems.SingleAsync(x => x.Id == reservation.InventoryItemId)).QuantityInStock);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsync($"{Jobs}/{Guid.NewGuid()}/complete", null)).StatusCode);
        var queued = await SeedJobAsync("Queued", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Client.PostAsync($"{Jobs}/{queued}/complete", null)).StatusCode);
    }

    [Fact]
    public async Task Fail_RecordsReasonOnce_AndRejectsInvalidRequests()
    {
        var id = await SeedJobAsync("InProgress", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsJsonAsync($"{Jobs}/{id}/fail", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsJsonAsync($"{Jobs}/{id}/fail", new { reason = "mechanical problem" })).StatusCode);
        var first = await LoadJobAsync(id);
        Assert.Equal("ExecutionFailed", first.Status);
        Assert.Equal("mechanical problem", first.FailureCode);
        Assert.NotNull(first.FailedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsJsonAsync($"{Jobs}/{id}/fail", new { reason = "duplicate" })).StatusCode);
        var repeated = await LoadJobAsync(id);
        Assert.Equal(first.FailedAt, repeated.FailedAt);
        Assert.Equal(first.FailureCode, repeated.FailureCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsJsonAsync($"{Jobs}/{Guid.NewGuid()}/fail", new { reason = "failure" })).StatusCode);
        var queued = await SeedJobAsync("Queued", DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Client.PostAsJsonAsync($"{Jobs}/{queued}/fail", new { reason = "failure" })).StatusCode);
    }

    private async Task<Guid> SeedJobAsync(string status, DateTime queuedAt, bool withReservation = false)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogInventoryDbContext>();
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var job = new ExecutionJob(id, correlationId, "[]", queuedAt);
        if (status != "Reserved") job.Queue("[]", queuedAt);
        if (status is "InProgress" or "Completed") job.Start(queuedAt.AddSeconds(1));
        if (status == "Completed") job.ChangeStatus("Completed", queuedAt.AddSeconds(2));
        db.ExecutionJobs.Add(job);
        if (withReservation)
        {
            var sku = Guid.NewGuid().ToString("N");
            var item = InventoryItem.Register(sku, "Part", "Part", 1m, 2, 0).Value;
            db.InventoryItems.Add(item);
            db.InventoryReservations.Add(InventoryReservation.Create(id, correlationId, item.Id, 1, queuedAt).Value);
        }
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<ExecutionJob> LoadJobAsync(Guid id)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogInventoryDbContext>();
        return await db.ExecutionJobs.AsNoTracking().SingleAsync(x => x.WorkOrderId == id);
    }
}
