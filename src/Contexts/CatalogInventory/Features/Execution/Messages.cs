namespace CatCar.Contexts.CatalogInventory.Features.Execution;

using Wolverine.Attributes;

public sealed record PartQuantity(Guid InventoryItemId, int Quantity);
public sealed record ServiceQuantity(Guid CatalogedServiceId, string Description, int Quantity);

[MessageIdentity("catcar.os.reserve-parts.v1")]
public sealed record ReserveParts(Guid WorkOrderId, IReadOnlyList<PartQuantity> Parts, Guid CorrelationId);
[MessageIdentity("catcar.os.release-parts.v1")]
public sealed record ReleaseParts(Guid WorkOrderId, string Reason, Guid CorrelationId);
[MessageIdentity("catcar.os.start-execution.v1")]
public sealed record StartExecution(Guid WorkOrderId, IReadOnlyList<ServiceQuantity> Services, IReadOnlyList<PartQuantity> Parts, Guid CorrelationId);
[MessageIdentity("catcar.execution.parts-reserved.v1")]
public sealed record PartsReserved(Guid WorkOrderId, DateTime ReservedAt, Guid CorrelationId);
[MessageIdentity("catcar.execution.parts-reservation-failed.v1")]
public sealed record PartsReservationFailed(Guid WorkOrderId, string FailureCode, DateTime FailedAt, Guid CorrelationId);
[MessageIdentity("catcar.execution.execution-completed.v1")]
public sealed record ExecutionCompleted(Guid WorkOrderId, DateTime CompletedAt, Guid CorrelationId);
[MessageIdentity("catcar.execution.execution-failed.v1")]
public sealed record ExecutionFailed(Guid WorkOrderId, string Reason, DateTime FailedAt, Guid CorrelationId);
