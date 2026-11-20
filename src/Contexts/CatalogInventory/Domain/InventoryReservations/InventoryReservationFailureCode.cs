namespace CatCar.Contexts.CatalogInventory.Domain.InventoryReservations;

public enum InventoryReservationFailureCode
{
    InsufficientStock,
    InventoryItemNotFound,
    InventoryItemInactive,
    InvalidQuantity
}
