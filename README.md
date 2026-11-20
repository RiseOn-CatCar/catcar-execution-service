# CatCar Execution Service

Service catalog, inventory, reservations and the execution queue for CatCar.
The service uses PostgreSQL (`ConnectionStrings__execution`) and RabbitMQ
(`ConnectionStrings__messaging`). Administrative endpoints validate the OS
service's JWT using `Jwt__Secret`, `Jwt__Issuer`, and `Jwt__Audience`.

Run `dotnet run --project src/Api/CatCar.Execution.Api.csproj -- --migrate`
to apply database migrations before starting the API. The public administrative
routes are under `/api/v1/catalog-inventory` and `/api/v1/execution`. Internal
catalog lookups under `/internal/v1/catalog` must remain accessible only within
the cluster. Health probes are `/health/live` and `/health/ready`.
