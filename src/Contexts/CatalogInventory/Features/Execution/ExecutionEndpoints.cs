namespace CatCar.Contexts.CatalogInventory.Features.Execution;

using CatCar.Contexts.CatalogInventory.Domain.CatalogedServices;
using CatCar.Contexts.CatalogInventory.Domain.InventoryItems;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

public sealed record FailExecutionRequest(string Reason);

public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/v1/catalog/services/{id:guid}", async Task<IResult> (Guid id, ICatalogedServiceRepository repository, CancellationToken ct) =>
        {
            var item = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Ok(new { id = item.Id, description = item.Name, unitPrice = item.Price.Amount, isActive = item.IsActive });
        });
        endpoints.MapGet("/internal/v1/catalog/parts/{id:guid}", async Task<IResult> (Guid id, IInventoryItemRepository repository, CancellationToken ct) =>
        {
            var item = await repository.GetByIdAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Ok(new { id = item.Id, description = item.Name, unitPrice = item.UnitPrice.Amount, isActive = item.IsActive, quantityInStock = item.QuantityInStock });
        });
        var group = endpoints.MapGroup("/api/v1/execution").RequireAuthorization();
        group.MapGet("/jobs", ListAsync);
        group.MapGet("/jobs/{workOrderId:guid}", async Task<IResult> (Guid workOrderId, CatalogInventoryDbContext db, CancellationToken ct) =>
        {
            var job = await db.ExecutionJobs.AsNoTracking().SingleOrDefaultAsync(x => x.WorkOrderId == workOrderId, ct).ConfigureAwait(false);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });
        group.MapPost("/jobs/{workOrderId:guid}/start", StartAsync);
        group.MapPost("/jobs/{workOrderId:guid}/complete", CompleteAsync);
        group.MapPost("/jobs/{workOrderId:guid}/fail", async Task<IResult> (Guid workOrderId, FailExecutionRequest request, CatalogInventoryDbContext db,
            IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest();
            var job = await db.ExecutionJobs.FindAsync([workOrderId], ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound();
            if (job.Status == "ExecutionFailed") return Results.NoContent();
            if (job.Status != "InProgress") return Results.Conflict();
            var now = DateTime.UtcNow;
            job.ChangeStatus("ExecutionFailed", now, request.Reason);
            await outbox.PublishAsync(new ExecutionFailed(workOrderId, request.Reason, now, job.CorrelationId)).ConfigureAwait(false);
            await outbox.SaveChangesAndFlushMessagesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        return endpoints;
    }

    public static async Task<IReadOnlyList<ExecutionJob>> ListAsync(string? status, CatalogInventoryDbContext db, CancellationToken ct)
    {
        var query = db.ExecutionJobs.AsNoTracking().Where(x => x.QueuedAt != null);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return await query.OrderBy(x => x.QueuedAt).ThenBy(x => x.WorkOrderId).ToListAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IResult> StartAsync(Guid workOrderId, CatalogInventoryDbContext db,
        IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken ct)
    {
        var job = await db.ExecutionJobs.FindAsync([workOrderId], ct).ConfigureAwait(false);
        if (job is null || job.QueuedAt is null) return Results.NotFound();
        if (job.Status == "InProgress") return Results.NoContent();
        if (job.Status != "Queued") return Results.Conflict();
        job.Start(DateTime.UtcNow);
        await outbox.SaveChangesAndFlushMessagesAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    public static async Task<IResult> CompleteAsync(Guid workOrderId, CatalogInventoryDbContext db,
        IInventoryItemRepository inventory, IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken ct)
    {
        var job = await db.ExecutionJobs.FindAsync([workOrderId], ct).ConfigureAwait(false);
        if (job is null) return Results.NotFound();
        if (job.Status == "Completed") return Results.NoContent();
        if (job.Status != "InProgress") return Results.Conflict();
        var reservations = await db.InventoryReservations
            .Where(x => x.WorkOrderId == workOrderId && x.Status == InventoryReservationStatus.Reserved)
            .ToListAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var result = await inventory.PrepareConsumptionAsync(reservations, now, ct).ConfigureAwait(false);
        if (result.IsFailure) throw new InvalidOperationException(result.Error.Message);
        job.ChangeStatus("Completed", now);
        await outbox.PublishAsync(new ExecutionCompleted(workOrderId, now, job.CorrelationId)).ConfigureAwait(false);
        await outbox.SaveChangesAndFlushMessagesAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}
