using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payouts.Accounts;
using Payouts.Messaging;
using Payouts.Payments;
using Payouts.Transfers;
using Xunit;

namespace Payouts.Tests;

// The three real hosts over RabbitMQ, each with its own database.
public sealed class JourneyTests(PostgresFixture postgres, RabbitFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approved_transfer_completes()
    {
        var (transfers, accountsDb, apps) = await StartAsync();
        await using var _ = apps;

        var transfer = await RunTransferAsync(transfers, AccountsDbContext.RichAccount, "DEMOAPPROVE");

        Assert.Equal("Completed", transfer.Outcome);
        Assert.StartsWith("SIM-", transfer.BankReference);
        Assert.Equal(975m, await AvailableAsync(accountsDb, AccountsDbContext.RichAccount));
    }

    [Fact]
    public async Task Rejected_transfer_releases_the_funds()
    {
        var (transfers, accountsDb, apps) = await StartAsync();
        await using var _ = apps;

        var transfer = await RunTransferAsync(transfers, AccountsDbContext.PoorAccount, "DEMOREJECT");

        Assert.Equal(("Failed", "BeneficiaryRejected"), (transfer.Outcome, transfer.FailureReason));
        Assert.Equal(50m, await AvailableAsync(accountsDb, AccountsDbContext.PoorAccount));
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_without_side_effects()
    {
        var (transfers, accountsDb, apps) = await StartAsync();
        await using var _ = apps;

        object[] requests =
        [
            new { id = Guid.Empty, sourceAccountId = AccountsDbContext.PoorAccount, beneficiary = "DEMOAPPROVE", amount = 1m, currency = "EUR" },
            new { id = Guid.NewGuid(), sourceAccountId = AccountsDbContext.PoorAccount, beneficiary = "DEMOREJECT", amount = 10.005m, currency = "EUR" },
        ];
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.BadRequest, (await transfers.PostAsJsonAsync("/transfers", request, Ct)).StatusCode);

        await Task.Delay(500, Ct);
        Assert.Equal(50m, await AvailableAsync(accountsDb, AccountsDbContext.PoorAccount));
    }

    [Fact]
    public async Task Concurrent_posts_with_one_id_create_one_transfer()
    {
        var (transfers, accountsDb, apps) = await StartAsync();
        await using var _ = apps;

        var id = Guid.NewGuid();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
            (await transfers.PostAsJsonAsync("/transfers", new
            {
                id, sourceAccountId = AccountsDbContext.RichAccount, beneficiary = "DEMOAPPROVE",
                amount = i % 2 == 0 ? 25m : 30m, currency = "EUR",
            }, Ct)).StatusCode));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.All(statuses, s => Assert.Contains(s, new[] { HttpStatusCode.Accepted, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        await Wait.UntilAsync(async () =>
            (await transfers.GetFromJsonAsync<TransferView>($"/transfers/{id}", Ct))!.Outcome != "Pending", $"transfer {id}");
        Assert.Contains(await AvailableAsync(accountsDb, AccountsDbContext.RichAccount), new[] { 975m, 970m });
    }

    private static async Task<TransferView> RunTransferAsync(HttpClient client, Guid accountId, string beneficiary)
    {
        var id = Guid.NewGuid();
        var created = await client.PostAsJsonAsync("/transfers",
            new { id, sourceAccountId = accountId, beneficiary, amount = 25.00m, currency = "EUR" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        TransferView? view = null;
        await Wait.UntilAsync(async () =>
        {
            view = await client.GetFromJsonAsync<TransferView>(created.Headers.Location, Ct);
            return view!.Outcome != "Pending";
        }, $"transfer {id}");
        return view!;
    }

    private async Task<(HttpClient Transfers, string AccountsDb, Hosts Apps)> StartAsync()
    {
        var vhost = await rabbit.NewVirtualHostAsync();
        var accountsDb = await postgres.NewDatabaseAsync();
        var paymentsDb = await postgres.NewDatabaseAsync();
        var transfersDb = await postgres.NewDatabaseAsync();

        var accounts = AccountsApp.Build([], b => Configure(b, accountsDb, vhost));
        await accounts.PrepareDatabaseAsync(Ct);
        var payments = PaymentsApp.Build([], b => Configure(b, paymentsDb, vhost));
        await payments.EnsureServiceSchemaAsync<PaymentsDbContext>(Ct);
        var transfers = TransfersApp.Build([], b =>
        {
            Configure(b, transfersDb, vhost);
            b.Configuration["SagaTimeouts:AccountsReply"] = "00:00:10";
            b.Configuration["SagaTimeouts:PaymentsReply"] = "00:00:15";
        });
        await transfers.EnsureServiceSchemaAsync<TransfersDbContext>(Ct);

        var hosts = new Hosts([accounts, payments, transfers]);
        foreach (var app in hosts.Apps)
            await app.StartAsync(Ct);

        var address = transfers.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (new HttpClient { BaseAddress = new Uri(address) }, accountsDb, hosts);
    }

    private void Configure(WebApplicationBuilder builder, string connectionString, string vhost)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = "http://127.0.0.1:0",
            ["ConnectionStrings:ServiceDb"] = connectionString,
            ["RabbitMq:Host"] = rabbit.Host,
            ["RabbitMq:Port"] = rabbit.Port.ToString(),
            ["RabbitMq:User"] = RabbitFixture.User,
            ["RabbitMq:Password"] = RabbitFixture.Password,
            ["RabbitMq:VirtualHost"] = vhost,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
    }

    private static async Task<decimal> AvailableAsync(string connectionString, Guid accountId)
    {
        await using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(connectionString).Options);
        return await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Available).SingleAsync(Ct);
    }

    private sealed record Hosts(WebApplication[] Apps) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var app in Apps.Reverse())
                await app.DisposeAsync();
        }
    }
}
