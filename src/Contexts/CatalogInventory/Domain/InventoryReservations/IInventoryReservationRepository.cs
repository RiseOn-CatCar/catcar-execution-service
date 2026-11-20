namespace CatCar.Contexts.CatalogInventory.Domain.InventoryReservations;

public interface IInventoryReservationRepository
{
    Task<IReadOnlyList<InventoryReservation>> GetActiveReservationsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryReservation>> GetActiveReservationsByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryReservation>> GetReservationsByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task AddAsync(InventoryReservation reservation, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<InventoryReservation> reservations, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
