using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Payouts.Contracts;
using Wolverine;

namespace Payouts.Payments;

public sealed class PaymentLog;

/// <summary>
/// The simulated bank, and the saga's pivot. The first stored decision for a transfer wins, and every reply is read
/// back from it, so a late submit after a cancel (or the other way round) can't contradict the outcome.
/// DEMOAPPROVE is accepted, DEMOREJECT rejected, and DEMOTIMEOUT never answered.
/// </summary>
public static partial class PaymentHandler
{
    public static async Task<OutgoingMessages> Handle(SubmitPayment command, Envelope envelope, PaymentsDbContext db,
        TimeProvider clock, ILogger<PaymentLog> logger, CancellationToken ct) =>
        Reply(logger, envelope, command, await SubmitAsync(db, command, clock.GetUtcNow(), ct));

    public static async Task<OutgoingMessages> Handle(CancelPayment command, Envelope envelope, PaymentsDbContext db,
        TimeProvider clock, ILogger<PaymentLog> logger, CancellationToken ct) =>
        Reply(logger, envelope, command, await CancelAsync(db, command, clock.GetUtcNow(), ct));

    /// <returns>The reply, or null for DEMOTIMEOUT when nothing is decided yet.</returns>
    public static async Task<ICorrelatedByTransfer?> SubmitAsync(PaymentsDbContext db, SubmitPayment command, DateTimeOffset now,
        CancellationToken ct)
    {
        if (command.Beneficiary == "DEMOTIMEOUT")
            return (await FindAsync(db, command.TransferId, ct))?.ToReply();

        var approved = command.Beneficiary == "DEMOAPPROVE";
        return await DecideOnceAsync(db, new Payment
        {
            TransferId = command.TransferId,
            Status = approved ? PaymentStatus.Accepted : PaymentStatus.Rejected,
            Beneficiary = command.Beneficiary,
            Amount = command.Amount,
            BankReference = approved ? "SIM-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)) : null,
            Reason = approved ? null : PaymentRejectionReason.BeneficiaryRejected,
            CreatedAt = now,
        }, ct);
    }

    public static Task<ICorrelatedByTransfer> CancelAsync(PaymentsDbContext db, CancelPayment command, DateTimeOffset now,
        CancellationToken ct) =>
        DecideOnceAsync(db, new Payment { TransferId = command.TransferId, Status = PaymentStatus.Cancelled, CreatedAt = now }, ct);

    private static async Task<ICorrelatedByTransfer> DecideOnceAsync(PaymentsDbContext db, Payment decision, CancellationToken ct)
    {
        // ON CONFLICT DO NOTHING: a competing insert waits for the other transaction, then loses quietly.
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO payments (transfer_id, status, beneficiary, amount, bank_reference, reason, created_at)
            VALUES ({decision.TransferId}, {decision.Status.ToString()}, {decision.Beneficiary}, {decision.Amount},
                    {decision.BankReference}, {decision.Reason?.ToString()}, {decision.CreatedAt})
            ON CONFLICT (transfer_id) DO NOTHING
            """, ct) == 1;
        return inserted ? decision.ToReply() : (await FindAsync(db, decision.TransferId, ct))!.ToReply();
    }

    private static Task<Payment?> FindAsync(PaymentsDbContext db, Guid transferId, CancellationToken ct) =>
        db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.TransferId == transferId, ct);

    private static OutgoingMessages Reply(ILogger logger, Envelope envelope, ICorrelatedByTransfer command, ICorrelatedByTransfer? reply)
    {
        LogHandled(logger, command.TransferId, envelope.Id, command.GetType().Name, reply?.GetType().Name ?? "none");
        return reply is null ? [] : [reply];
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Transfer {TransferId}: handled {MessageType} {MessageId}, reply {ReplyType}")]
    private static partial void LogHandled(ILogger logger, Guid transferId, Guid messageId, string messageType, string replyType);
}
