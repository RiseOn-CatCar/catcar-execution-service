namespace CatCar.Contexts.CatalogInventory.Features.Execution;

public sealed class ExecutionJob
{
    public Guid WorkOrderId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string PartsJson { get; private set; } = "[]";
    public string ServicesJson { get; private set; } = "[]";
    public string? FailureCode { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? QueuedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }

    private ExecutionJob() { }

    public ExecutionJob(Guid workOrderId, Guid correlationId, string partsJson, DateTime now)
    {
        WorkOrderId = workOrderId;
        Reset(correlationId, partsJson, now);
    }

    public void Reset(Guid correlationId, string partsJson, DateTime now)
    {
        CorrelationId = correlationId;
        PartsJson = partsJson;
        ServicesJson = "[]";
        FailureCode = null;
        QueuedAt = null;
        StartedAt = null;
        CompletedAt = null;
        FailedAt = null;
        Status = "Reserved";
        UpdatedAt = now;
    }

    public void ChangeStatus(string status, DateTime now, string? failureCode = null)
    {
        Status = status;
        FailureCode = failureCode;
        UpdatedAt = now;
        if (status == "Completed") CompletedAt = now;
        if (status == "ExecutionFailed") FailedAt = now;
    }

    public void Queue(string servicesJson, DateTime now)
    {
        ServicesJson = servicesJson;
        QueuedAt = now;
        ChangeStatus("Queued", now);
    }

    public void Start(DateTime now)
    {
        StartedAt = now;
        ChangeStatus("InProgress", now);
    }
}
