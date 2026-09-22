using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Oracle.ManagedDataAccess.Client;

namespace BiDataAccess;

/// <summary>
/// A small provider-neutral data-access client for SQL Server, SQLite and Oracle.
/// Create one instance per configured database; connections are opened only for the operation.
/// </summary>
public sealed class DatabaseClient
{
    private readonly string _connectionString;
    private readonly DbProviderFactory _factory;

    public DatabaseProvider Provider { get; }

    public DatabaseClient(DatabaseProvider provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A connection string is required.", nameof(connectionString));

        Provider = provider;
        _connectionString = connectionString;
        _factory = provider switch
        {
            DatabaseProvider.SqlServer => SqlClientFactory.Instance,
            DatabaseProvider.Sqlite => SqliteFactory.Instance,
            DatabaseProvider.Oracle => OracleClientFactory.Instance,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider.")
        };
    }

    public async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T?> ScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<DbDataReader, T> map,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(map(reader));
        return results;
    }

    public async Task<IReadOnlyList<string>> GetColumnsAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    }

    public async Task<TResult> InTransactionAsync<TResult>(
        Func<DbConnection, DbTransaction, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _factory.CreateConnection()
            ?? throw new InvalidOperationException("The database provider could not create a connection.");
        connection.ConnectionString = _connectionString;
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private DbCommand CreateCommand(
        DbConnection connection,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL is required.", nameof(sql));

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;

        if (parameters is null) return command;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = NormalizeParameterName(name);
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private string NormalizeParameterName(string name)
    {
        var bareName = name.TrimStart('@', ':');
        return Provider == DatabaseProvider.Oracle ? $":{bareName}" : $"@{bareName}";
    }
}
