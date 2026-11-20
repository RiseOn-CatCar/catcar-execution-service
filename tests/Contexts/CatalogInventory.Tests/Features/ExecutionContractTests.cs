namespace CatCar.Contexts.CatalogInventory.Tests.Features;

using System.Text.Json;
using CatCar.Contexts.CatalogInventory.Features.Execution;
using Wolverine.Attributes;
using Xunit;

public sealed class ExecutionContractTests
{
    private static readonly string[] PartFields = ["inventoryItemId", "quantity"];
    private static readonly string[] ServiceFields = ["catalogedServiceId", "description", "quantity"];

    [Theory]
    [InlineData(typeof(ReserveParts), "catcar.os.reserve-parts.v1", "workOrderId,parts,correlationId")]
    [InlineData(typeof(ReleaseParts), "catcar.os.release-parts.v1", "workOrderId,reason,correlationId")]
    [InlineData(typeof(StartExecution), "catcar.os.start-execution.v1", "workOrderId,services,parts,correlationId")]
    [InlineData(typeof(PartsReserved), "catcar.execution.parts-reserved.v1", "workOrderId,reservedAt,correlationId")]
    [InlineData(typeof(PartsReservationFailed), "catcar.execution.parts-reservation-failed.v1", "workOrderId,failureCode,failedAt,correlationId")]
    [InlineData(typeof(ExecutionCompleted), "catcar.execution.execution-completed.v1", "workOrderId,completedAt,correlationId")]
    [InlineData(typeof(ExecutionFailed), "catcar.execution.execution-failed.v1", "workOrderId,reason,failedAt,correlationId")]
    public void Messages_HaveStableIdentityAndJsonFields(Type type, string identity, string fields)
    {
        var attribute = Assert.Single(type.CustomAttributes, x => x.AttributeType == typeof(MessageIdentityAttribute));
        Assert.Equal(identity, attribute.ConstructorArguments[0].Value);
        var constructor = Assert.Single(type.GetConstructors());
        var args = constructor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var message = constructor.Invoke(args);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(message, type, JsonSerializerOptions.Web));
        Assert.Equal(fields.Split(','), json.RootElement.EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void NestedQuantities_HaveStableJsonFields()
    {
        using var part = JsonDocument.Parse(JsonSerializer.Serialize(new PartQuantity(Guid.NewGuid(), 1), JsonSerializerOptions.Web));
        using var service = JsonDocument.Parse(JsonSerializer.Serialize(new ServiceQuantity(Guid.NewGuid(), "service", 1), JsonSerializerOptions.Web));
        Assert.Equal(PartFields, part.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(ServiceFields, service.RootElement.EnumerateObject().Select(x => x.Name));
    }
}
