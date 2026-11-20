namespace CatCar.Contexts.CatalogInventory.Features.Execution;

using System.Text.Json;
using CatCar.Contexts.CatalogInventory.Domain.InventoryItems;
using CatCar.Contexts.CatalogInventory.Domain.InventoryReservations;
using CatCar.Contexts.CatalogInventory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

public static class ExecutionHandlers
{
    public static async Task Handle(ReserveParts command, CatalogInventoryDbContext db,
        IInventoryItemRepository inventory, IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken cancellationToken)
    {
        var job = await db.ExecutionJobs.FindAsync([command.WorkOrderId], cancellationToken).ConfigureAwait(false);
        if (job is not null && job.CorrelationId == command.CorrelationId) return;
        if (job is not null && job.Status is not ("ReservationFailed" or "Released"))
            throw new InvalidOperationException("A reservation already exists for this work order.");

        var now = DateTime.UtcNow;
        var parts = new List<PartQuantity>();
        string? failure = null;
        if (command.WorkOrderId == Guid.Empty || command.CorrelationId == Guid.Empty || command.Parts is null ||
            command.Parts.Any(x => x.InventoryItemId == Guid.Empty || x.Quantity <= 0))
            failure = "InvalidParts";
        else
        {
            foreach (var group in command.Parts.GroupBy(x => x.InventoryItemId).OrderBy(x => x.Key))
            {
                var quantity = group.Sum(x => (long)x.Quantity);
                if (quantity > int.MaxValue) { failure = "InvalidParts"; break; }
                parts.Add(new PartQuantity(group.Key, (int)quantity));
            }
        }

        var json = JsonSerializer.Serialize(parts);
        if (job is null)
        {
            job = new ExecutionJob(command.WorkOrderId, command.CorrelationId, json, now);
            db.ExecutionJobs.Add(job);
        }
        else job.Reset(command.CorrelationId, json, now);

        if (failure is null)
        {
            var reservations = new List<InventoryReservation>(parts.Count);
            foreach (var part in parts)
            {
                var result = InventoryReservation.Create(command.WorkOrderId, command.CorrelationId, part.InventoryItemId, part.Quantity, now);
                if (result.IsFailure) { failure = "InvalidParts"; break; }
                reservations.Add(result.Value);
            }
            if (failure is null)
                failure = (await inventory.PrepareReservationAsync(reservations, cancellationToken).ConfigureAwait(false))?.ToString();
        }
        if (failure is null)
            await outbox.PublishAsync(new PartsReserved(command.WorkOrderId, now, command.CorrelationId)).ConfigureAwait(false);
        else
        {
            job.ChangeStatus("ReservationFailed", now, failure);
            await outbox.PublishAsync(new PartsReservationFailed(command.WorkOrderId, failure, now, command.CorrelationId)).ConfigureAwait(false);
        }
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task Handle(ReleaseParts command, CatalogInventoryDbContext db,
        IInventoryItemRepository inventory, IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken cancellationToken)
    {
        var reservations = await db.InventoryReservations
            .Where(x => x.WorkOrderId == command.WorkOrderId && x.Status == InventoryReservationStatus.Reserved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (reservations.Count > 0)
        {
            var result = await inventory.PrepareReleaseAsync(reservations, now, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) throw new InvalidOperationException(result.Error.Message);
        }
        var job = await db.ExecutionJobs.FindAsync([command.WorkOrderId], cancellationToken).ConfigureAwait(false);
        if (job is not null && job.Status is not ("Released" or "Completed" or "ReservationFailed"))
            job.ChangeStatus("Released", now);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task Handle(StartExecution command, CatalogInventoryDbContext db,
        IDbContextOutbox<CatalogInventoryDbContext> outbox, CancellationToken cancellationToken)
    {
        var job = await db.ExecutionJobs.FindAsync([command.WorkOrderId], cancellationToken).ConfigureAwait(false);
        if (job is not null && job.CorrelationId != command.CorrelationId) return;
        if (job is not null && job.Status is "Queued" or "InProgress" or "Completed" or "ExecutionFailed" or "Released") return;
        var now = DateTime.UtcNow;
        var partsValid = command.Parts is not null && command.Parts.All(x => x.InventoryItemId != Guid.Empty && x.Quantity > 0);
        var normalized = partsValid ? command.Parts!.GroupBy(x => x.InventoryItemId).OrderBy(x => x.Key)
            .Select(g => new { Id = g.Key, Quantity = g.Sum(x => (long)x.Quantity) }).ToArray() : [];
        var reserved = job is null ? [] : (JsonSerializer.Deserialize<List<PartQuantity>>(job.PartsJson) ?? [])
            .Select(x => new { Id = x.InventoryItemId, Quantity = (long)x.Quantity }).ToArray();
        if (job is null || job.CorrelationId != command.CorrelationId || job.Status != "Reserved" || !partsValid ||
            !normalized.SequenceEqual(reserved) || command.Services is null ||
            command.Services.Any(x => x.CatalogedServiceId == Guid.Empty || x.Quantity <= 0 || string.IsNullOrWhiteSpace(x.Description)))
        {
            if (job is null)
            {
                job = new ExecutionJob(command.WorkOrderId, command.CorrelationId, "[]", now);
                db.ExecutionJobs.Add(job);
            }
            job.ChangeStatus("ExecutionFailed", now, "InvalidExecution");
            await outbox.PublishAsync(new ExecutionFailed(command.WorkOrderId, "InvalidExecution", now, command.CorrelationId)).ConfigureAwait(false);
        }
        else job.Queue(JsonSerializer.Serialize(command.Services), now);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken).ConfigureAwait(false);
    }
}
