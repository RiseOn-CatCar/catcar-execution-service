namespace CatCar.Contexts.CatalogInventory.Tests.Integration;

using CatCar.Contexts.CatalogInventory.Domain.InventoryItems;
using CatCar.Contexts.CatalogInventory.Domain.InventoryReservations;
using CatCar.Contexts.CatalogInventory.Features.Execution;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using CatCar.Contexts.CatalogInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Xunit;

public sealed class ExecutionWorkflowTests : IntegrationTestBase
{
    [Fact]
    public async Task Reservation_WhenOnePartIsUnavailable_DoesNotDeductAnyStock()
    {
        var first = await AddItemAsync("first", 5);
        var second = await AddItemAsync("second", 1);
        var command = new ReserveParts(Guid.NewGuid(), [new(first, 2), new(second, 2)], Guid.NewGuid());
        await WithHostAsync(host => ReserveAsync(host, command));
        await using var db = CreateDbContext();
        Assert.Equal(5, (await db.InventoryItems.SingleAsync(x => x.Id == first)).QuantityInStock);
        Assert.Equal(1, (await db.InventoryItems.SingleAsync(x => x.Id == second)).QuantityInStock);
        Assert.Empty(await db.InventoryReservations.ToListAsync());
        Assert.Equal("ReservationFailed", (await db.ExecutionJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReservationAndRelease_Redelivered_ChangeStockOnlyOnce()
    {
        var item = await AddItemAsync("part", 5);
        var command = new ReserveParts(Guid.NewGuid(), [new(item, 1), new(item, 1)], Guid.NewGuid());
        await WithHostAsync(async host =>
        {
            await ReserveAsync(host, command);
            await ReserveAsync(host, command);
            await ReleaseAsync(host, new ReleaseParts(command.WorkOrderId, "cancelled", command.CorrelationId));
            await ReleaseAsync(host, new ReleaseParts(command.WorkOrderId, "cancelled", command.CorrelationId));
        });
        await using var db = CreateDbContext();
        Assert.Equal(5, (await db.InventoryItems.SingleAsync()).QuantityInStock);
        Assert.Equal(InventoryReservationStatus.Released, (await db.InventoryReservations.SingleAsync()).Status);
    }
    [Fact]
    public async Task Release_ByWorkOrderId_ReleasesAllReservedPartsOfThatWorkOrderOnly()
    {
        var first = await AddItemAsync("release-first", 10);
        var second = await AddItemAsync("release-second", 10);
        var target = new ReserveParts(Guid.NewGuid(), [new(first, 3), new(second, 4)], Guid.NewGuid());
        var other = new ReserveParts(Guid.NewGuid(), [new(first, 2)], Guid.NewGuid());
        await WithHostAsync(async host =>
        {
            await ReserveAsync(host, target);
            await ReserveAsync(host, other);
            // The release carries an unrelated correlation id: the work order id alone selects the reservations.
            await ReleaseAsync(host, new ReleaseParts(target.WorkOrderId, "cancelled", Guid.NewGuid()));
            await ReleaseAsync(host, new ReleaseParts(target.WorkOrderId, "cancelled", Guid.NewGuid()));
        });
        await using var db = CreateDbContext();
        Assert.Equal(8, (await db.InventoryItems.SingleAsync(x => x.Id == first)).QuantityInStock);
        Assert.Equal(10, (await db.InventoryItems.SingleAsync(x => x.Id == second)).QuantityInStock);
        var reservations = await db.InventoryReservations.ToListAsync();
        Assert.All(reservations.Where(x => x.WorkOrderId == target.WorkOrderId), x => Assert.Equal(InventoryReservationStatus.Released, x.Status));
        Assert.Equal(2, reservations.Count(x => x.WorkOrderId == target.WorkOrderId));
        Assert.Equal(InventoryReservationStatus.Reserved, Assert.Single(reservations, x => x.WorkOrderId == other.WorkOrderId).Status);
        Assert.Equal("Released", (await db.ExecutionJobs.SingleAsync(x => x.WorkOrderId == target.WorkOrderId)).Status);
        Assert.Equal("Reserved", (await db.ExecutionJobs.SingleAsync(x => x.WorkOrderId == other.WorkOrderId)).Status);
    }


    [Fact]
    public async Task EmptyReservation_QueuesAndStartsExecutionWithoutStock()
    {
        var command = new ReserveParts(Guid.NewGuid(), [], Guid.NewGuid());
        await WithHostAsync(async host =>
        {
            await ReserveAsync(host, command);
            await using var scope = host.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
            await ExecutionHandlers.Handle(new StartExecution(command.WorkOrderId, [], [], command.CorrelationId), outbox.DbContext, outbox, CancellationToken.None);
            await ExecutionHandlers.Handle(new StartExecution(command.WorkOrderId, [], [], command.CorrelationId), outbox.DbContext, outbox, CancellationToken.None);
        });
        await using var db = CreateDbContext();
        Assert.Empty(await db.InventoryReservations.ToListAsync());
        var job = await db.ExecutionJobs.SingleAsync();
        Assert.Equal("Queued", job.Status);
        Assert.NotNull(job.QueuedAt);
        Assert.Null(job.StartedAt);
    }

    [Fact]
    public async Task ListJobs_FiltersStatusAndOrdersByQueueTime()
    {
        var now = DateTime.UtcNow;
        var earlier = new ExecutionJob(Guid.NewGuid(), Guid.NewGuid(), "[]", now);
        earlier.Queue("[]", now.AddMinutes(-2));
        var later = new ExecutionJob(Guid.NewGuid(), Guid.NewGuid(), "[]", now);
        later.Queue("[]", now.AddMinutes(-1));
        var running = new ExecutionJob(Guid.NewGuid(), Guid.NewGuid(), "[]", now);
        running.Queue("[]", now.AddMinutes(-3));
        running.Start(now);
        var reserved = new ExecutionJob(Guid.NewGuid(), Guid.NewGuid(), "[]", now);
        await using var db = CreateDbContext();
        db.ExecutionJobs.AddRange(later, running, reserved, earlier);
        await db.SaveChangesAsync();
        var queued = await ExecutionEndpoints.ListAsync("Queued", db, CancellationToken.None);
        Assert.Equal(new[] { earlier.WorkOrderId, later.WorkOrderId }, queued.Select(x => x.WorkOrderId));
        Assert.Equal(3, (await ExecutionEndpoints.ListAsync(null, db, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task CompetingReservations_ForLastItem_OnlyOneSucceeds()
    {
        var item = await AddItemAsync("last", 1);
        var first = new ReserveParts(Guid.NewGuid(), [new(item, 1)], Guid.NewGuid());
        var second = new ReserveParts(Guid.NewGuid(), [new(item, 1)], Guid.NewGuid());
        await WithHostAsync(host => Task.WhenAll(ReserveWithRetryAsync(host, first), ReserveWithRetryAsync(host, second)));
        await using var db = CreateDbContext();
        var jobs = await db.ExecutionJobs.ToListAsync();
        Assert.Single(jobs, x => x.Status == "Reserved");
        Assert.Single(jobs, x => x.Status == "ReservationFailed");
        Assert.Equal(0, (await db.InventoryItems.SingleAsync()).QuantityInStock);
        Assert.Single(await db.InventoryReservations.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRedelivery_ReservesOnlyOnce()
    {
        var item = await AddItemAsync("duplicate", 3);
        var command = new ReserveParts(Guid.NewGuid(), [new(item, 1)], Guid.NewGuid());
        await WithHostAsync(host => Task.WhenAll(ReserveWithRetryAsync(host, command), ReserveWithRetryAsync(host, command)));
        await using var db = CreateDbContext();
        Assert.Equal(2, (await db.InventoryItems.SingleAsync()).QuantityInStock);
        Assert.Single(await db.InventoryReservations.ToListAsync());
        Assert.Single(await db.ExecutionJobs.ToListAsync());
    }

    private static async Task ReserveWithRetryAsync(IHost host, ReserveParts command)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await ReserveAsync(host, command); return; }
            catch (DbUpdateException) when (attempt < 3) { }
        }
    }

    [Fact]
    public async Task CompletionAndRedelivery_ConsumeReservationsOnlyOnce()
    {
        var item = await AddItemAsync("completed", 4);
        var command = new ReserveParts(Guid.NewGuid(), [new(item, 2)], Guid.NewGuid());
        await WithHostAsync(async host =>
        {
            await ReserveAsync(host, command);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
                await ExecutionHandlers.Handle(new StartExecution(command.WorkOrderId, [], command.Parts, command.CorrelationId), outbox.DbContext, outbox, CancellationToken.None);
            }
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
                await ExecutionEndpoints.StartAsync(command.WorkOrderId, outbox.DbContext, outbox, CancellationToken.None);
                await ExecutionEndpoints.StartAsync(command.WorkOrderId, outbox.DbContext, outbox, CancellationToken.None);
            }
            for (var i = 0; i < 2; i++)
            {
                await using var scope = host.Services.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
                await ExecutionEndpoints.CompleteAsync(command.WorkOrderId, outbox.DbContext, new InventoryItemRepository(outbox.DbContext), outbox, CancellationToken.None);
            }
            await ReserveAsync(host, command);
        });
        await using var db = CreateDbContext();
        Assert.Equal(2, (await db.InventoryItems.SingleAsync()).QuantityInStock);
        Assert.Equal(InventoryReservationStatus.Consumed, (await db.InventoryReservations.SingleAsync()).Status);
        var job = await db.ExecutionJobs.SingleAsync();
        Assert.Equal("Completed", job.Status);
        Assert.NotNull(job.QueuedAt);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
    }

    private async Task<Guid> AddItemAsync(string sku, int stock)
    {
        var item = InventoryItem.Register(sku, sku, sku, 1m, stock, 0).Value;
        await using var db = CreateDbContext();
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private async Task WithHostAsync(Func<IHost, Task> action)
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(options =>
        {
            options.PersistMessagesWithPostgresql(ConnectionString, schemaName: "wolverine");
            options.UseEntityFrameworkCoreTransactions();
            options.PublishMessage<PartsReserved>().ToLocalQueue("parts_reserved_test");
            options.PublishMessage<PartsReservationFailed>().ToLocalQueue("parts_failed_test");
            options.PublishMessage<ExecutionFailed>().ToLocalQueue("execution_failed_test");
            options.PublishMessage<ExecutionCompleted>().ToLocalQueue("execution_completed_test");
            options.UseRuntimeCompilation();
        }).ConfigureServices(services => services.AddDbContextWithWolverineIntegration<CatalogInventoryDbContext>(options =>
        {
            options.UseNpgsql(ConnectionString);
            options.AddInterceptors(new AuditInterceptor());
        }, wolverineDatabaseSchema: "wolverine")).Build();
        await host.StartAsync();
        try { await action(host); }
        finally { await host.StopAsync(); }
    }

    private static async Task ReserveAsync(IHost host, ReserveParts command)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
        await ExecutionHandlers.Handle(command, outbox.DbContext, new InventoryItemRepository(outbox.DbContext), outbox, CancellationToken.None);
    }

    private static async Task ReleaseAsync(IHost host, ReleaseParts command)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CatalogInventoryDbContext>>();
        await ExecutionHandlers.Handle(command, outbox.DbContext, new InventoryItemRepository(outbox.DbContext), outbox, CancellationToken.None);
    }
}
