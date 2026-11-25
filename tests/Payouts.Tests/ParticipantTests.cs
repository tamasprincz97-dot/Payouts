using Microsoft.EntityFrameworkCore;
using Payouts.Accounts;
using Payouts.Contracts;
using Payouts.Payments;
using Xunit;

namespace Payouts.Tests;

// Accounts and Payments logic against a real PostgreSQL, without the broker.
public sealed class ParticipantTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Insufficient_funds_are_rejected_and_replayed_the_same()
    {
        await using var db = await AccountsAsync();
        var reserve = new ReserveFunds(Guid.NewGuid(), AccountsDbContext.PoorAccount, 100m);

        var first = await FundsHandler.ReserveAsync(db, reserve, Now, Ct);
        await db.SaveChangesAsync(Ct);
        var replay = await FundsHandler.ReserveAsync(db, reserve, Now, Ct);

        Assert.Equal(new FundsReservationFailed(reserve.TransferId, ReservationFailureReason.InsufficientFunds), first);
        Assert.Equal(first, replay);
    }

    [Fact]
    public async Task Duplicate_release_returns_funds_once()
    {
        await using var db = await AccountsAsync();
        var transferId = Guid.NewGuid();

        await FundsHandler.ReserveAsync(db, new ReserveFunds(transferId, AccountsDbContext.RichAccount, 100m), Now, Ct);
        await db.SaveChangesAsync(Ct);
        for (var i = 0; i < 2; i++)
        {
            await FundsHandler.ReleaseAsync(db, new ReleaseFunds(transferId), Now, Ct);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(1000m, await AvailableAsync(db, AccountsDbContext.RichAccount));
    }

    [Fact]
    public async Task Release_before_reserve_blocks_the_late_reserve()
    {
        await using var db = await AccountsAsync();
        var transferId = Guid.NewGuid();

        await FundsHandler.ReleaseAsync(db, new ReleaseFunds(transferId), Now, Ct);
        await db.SaveChangesAsync(Ct);
        var reply = await FundsHandler.ReserveAsync(db, new ReserveFunds(transferId, AccountsDbContext.RichAccount, 100m), Now, Ct);

        Assert.Equal(new FundsReservationFailed(transferId, ReservationFailureReason.ReleasedBeforeReserved), reply);
        Assert.Equal(1000m, await AvailableAsync(db, AccountsDbContext.RichAccount));
    }

    [Fact]
    public async Task First_payment_decision_wins()
    {
        await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(await postgres.NewDatabaseAsync()).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        var transferId = Guid.NewGuid();

        var cancel = await PaymentHandler.CancelAsync(db, new CancelPayment(transferId), Now, Ct);
        var lateSubmit = await PaymentHandler.SubmitAsync(db, new SubmitPayment(transferId, "DEMOAPPROVE", 25m), Now, Ct);

        Assert.Equal(new PaymentCancelled(transferId), cancel);
        Assert.Equal(cancel, lateSubmit);
    }

    private async Task<AccountsDbContext> AccountsAsync()
    {
        var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(await postgres.NewDatabaseAsync()).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        await db.SeedAsync(Ct);
        return db;
    }

    private static Task<decimal> AvailableAsync(AccountsDbContext db, Guid accountId) =>
        db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.Available).SingleAsync(Ct);
}
