using Microsoft.EntityFrameworkCore;
using Payouts.Contracts;
using Wolverine;

namespace Payouts.Accounts;

public sealed class FundsLog;

/// <summary>
/// Handles the funds commands. Wolverine wraps each handler in one transaction, so the balance, the reservation row,
/// the inbox marker and the reply commit together.
/// </summary>
public static partial class FundsHandler
{
    public static async Task<OutgoingMessages> Handle(ReserveFunds command, Envelope envelope, AccountsDbContext db,
        TimeProvider clock, ILogger<FundsLog> logger, CancellationToken ct) =>
        Reply(logger, envelope, command, await ReserveAsync(db, command, clock.GetUtcNow(), ct));

    public static async Task<OutgoingMessages> Handle(ReleaseFunds command, Envelope envelope, AccountsDbContext db,
        TimeProvider clock, ILogger<FundsLog> logger, CancellationToken ct) =>
        Reply(logger, envelope, command, await ReleaseAsync(db, command, clock.GetUtcNow(), ct));

    public static async Task<OutgoingMessages> Handle(SettleFunds command, Envelope envelope, AccountsDbContext db,
        TimeProvider clock, ILogger<FundsLog> logger, CancellationToken ct) =>
        Reply(logger, envelope, command, await SettleAsync(db, command, clock.GetUtcNow(), ct));

    public static async Task<ICorrelatedByTransfer> ReserveAsync(AccountsDbContext db, ReserveFunds command, DateTimeOffset now,
        CancellationToken ct)
    {
        if (await FindAsync(db, command.TransferId, ct) is { } stored)
            return stored.Status switch
            {
                ReservationStatus.Released => new FundsReservationFailed(command.TransferId, ReservationFailureReason.ReleasedBeforeReserved),
                ReservationStatus.Rejected => new FundsReservationFailed(command.TransferId, stored.Reason!.Value),
                _ => new FundsReserved(command.TransferId),
            };

        // Conditional update: two debits of one account can never overdraw it.
        var debited = await db.Accounts
            .Where(a => a.Id == command.AccountId && a.Available >= command.Amount)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Available, a => a.Available - command.Amount), ct) == 1;
        if (debited)
        {
            db.Reservations.Add(new Reservation
            {
                TransferId = command.TransferId, AccountId = command.AccountId, Amount = command.Amount,
                Status = ReservationStatus.Reserved, CreatedAt = now, UpdatedAt = now,
            });
            return new FundsReserved(command.TransferId);
        }

        // Store the rejection so a replay after the balance recovers still fails.
        var accountExists = await db.Accounts.AnyAsync(a => a.Id == command.AccountId, ct);
        var reason = accountExists ? ReservationFailureReason.InsufficientFunds : ReservationFailureReason.AccountNotFound;
        db.Reservations.Add(new Reservation
        {
            TransferId = command.TransferId, AccountId = accountExists ? command.AccountId : null, Amount = command.Amount,
            Status = ReservationStatus.Rejected, Reason = reason, CreatedAt = now, UpdatedAt = now,
        });
        return new FundsReservationFailed(command.TransferId, reason);
    }

    public static async Task<ICorrelatedByTransfer> ReleaseAsync(AccountsDbContext db, ReleaseFunds command, DateTimeOffset now,
        CancellationToken ct)
    {
        var reservation = await FindAsync(db, command.TransferId, ct);
        switch (reservation)
        {
            case null:
                // Release arrived before the reserve: leave a marker that refuses the late reserve.
                db.Reservations.Add(new Reservation
                {
                    TransferId = command.TransferId, Status = ReservationStatus.Released, CreatedAt = now, UpdatedAt = now,
                });
                break;
            case { Status: ReservationStatus.Reserved, AccountId: { } accountId }:
                await db.Accounts.Where(a => a.Id == accountId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Available, a => a.Available + reservation.Amount), ct);
                reservation.MoveTo(ReservationStatus.Released, now);
                break;
            case { Status: ReservationStatus.Settled }:
                throw new InvalidOperationException($"Transfer {command.TransferId}: release of a settled reservation");
            // Released or Rejected: nothing is held, just acknowledge.
        }
        return new FundsReleased(command.TransferId);
    }

    public static async Task<ICorrelatedByTransfer> SettleAsync(AccountsDbContext db, SettleFunds command, DateTimeOffset now,
        CancellationToken ct)
    {
        var reservation = await FindAsync(db, command.TransferId, ct);
        if (reservation?.Status is not (ReservationStatus.Reserved or ReservationStatus.Settled))
            throw new InvalidOperationException($"Transfer {command.TransferId}: settlement of a {reservation?.Status.ToString() ?? "missing"} reservation");
        if (reservation.Status == ReservationStatus.Reserved)
            reservation.MoveTo(ReservationStatus.Settled, now);
        return new FundsSettled(command.TransferId);
    }

    private static Task<Reservation?> FindAsync(AccountsDbContext db, Guid transferId, CancellationToken ct) =>
        db.Reservations.SingleOrDefaultAsync(r => r.TransferId == transferId, ct);

    private static OutgoingMessages Reply(ILogger logger, Envelope envelope, ICorrelatedByTransfer command, ICorrelatedByTransfer reply)
    {
        LogHandled(logger, command.TransferId, envelope.Id, command.GetType().Name, reply.GetType().Name);
        return [reply];
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Transfer {TransferId}: handled {MessageType} {MessageId}, reply {ReplyType}")]
    private static partial void LogHandled(ILogger logger, Guid transferId, Guid messageId, string messageType, string replyType);
}
