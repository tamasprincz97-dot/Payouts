using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

[assembly: AssemblyFixture(typeof(Payouts.Tests.PostgresFixture))]
[assembly: AssemblyFixture(typeof(Payouts.Tests.RabbitFixture))]

namespace Payouts.Tests;

// One container per run, started on first use so TransferTests need no Docker. Each test gets its own database.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11-alpine").Build();
    private readonly Lazy<Task> _started;

    public PostgresFixture() => _started = new(() => _container.StartAsync());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        await _started.Value;
        var ct = TestContext.Current.CancellationToken;
        var name = $"t_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await create.ExecuteNonQueryAsync(ct);
        // No pooling: one idle pool per test database would exhaust max_connections.
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}

// One broker per run; each test gets its own virtual host.
public sealed class RabbitFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.4-management-alpine").Build();
    private readonly Lazy<Task> _started;

    public RabbitFixture() => _started = new(() => _broker.StartAsync());

    public string Host => _broker.Hostname;
    public int Port => _broker.GetMappedPublicPort(5672);
    public const string User = "rabbitmq";
    public const string Password = "rabbitmq";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _broker.DisposeAsync();

    public async Task<string> NewVirtualHostAsync()
    {
        await _started.Value;
        var vhost = $"v_{Guid.NewGuid():N}";
        var ct = TestContext.Current.CancellationToken;
        var add = await _broker.ExecAsync(["rabbitmqctl", "add_vhost", vhost], ct);
        var grant = await _broker.ExecAsync(["rabbitmqctl", "set_permissions", "-p", vhost, User, ".*", ".*", ".*"], ct);
        if (add.ExitCode != 0 || grant.ExitCode != 0)
            throw new InvalidOperationException($"vhost setup failed: {add.Stderr} {grant.Stderr}");
        return vhost;
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string operation, TimeSpan? timeout = null)
    {
        var start = Stopwatch.StartNew();
        while (!await condition())
        {
            if (start.Elapsed >= (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException($"Timed out waiting for {operation}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
