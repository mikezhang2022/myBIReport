# BI data-access component

`DatabaseClient` provides the same async API for SQL Server, SQLite, and Oracle. Supply a provider and connection string, then call `ExecuteAsync`, `ScalarAsync`, `QueryAsync`, or `InTransactionAsync`.

## SQLite quick start

```csharp
using BiDataAccess;

var db = new DatabaseClient(DatabaseProvider.Sqlite, "Data Source=bi-demo.db");
await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS sales (id INTEGER PRIMARY KEY, amount REAL)");
await db.ExecuteAsync("INSERT INTO sales (amount) VALUES (@amount)", new Dictionary<string, object?>
{
    ["amount"] = 123.45m
});

var total = await db.ScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM sales");
```

## Connection examples

```csharp
new DatabaseClient(DatabaseProvider.SqlServer,
    "Server=localhost;Database=Bi;Integrated Security=True;TrustServerCertificate=True;");
new DatabaseClient(DatabaseProvider.Oracle,
    "User Id=bi_user;Password=***;Data Source=host:1521/service_name;");
new DatabaseClient(DatabaseProvider.Sqlite, "Data Source=bi.db");
```

Use named parameters rather than concatenating user input into SQL. Write `@customerId` / `@startDate` in SQL Server and SQLite statements, and `:customerId` / `:startDate` in Oracle statements. The dictionary keys can remain unprefixed on every provider.
