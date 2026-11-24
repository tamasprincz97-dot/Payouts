using System.ComponentModel.DataAnnotations;
using Payouts.Contracts;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Payouts.Transfers;

public enum TransferState
{
    ReservingFunds,
    SubmittingPayment,
    CancellingPayment,
    SettlingFunds,
    ReleasingFunds,
    Completed,
    Failed,
}

public enum TransferFailureReason
{
    InsufficientFunds,
    AccountNotFound,
    ReleasedBeforeReserved,
    ReservationTimedOut,
    BeneficiaryRejected,
    PaymentCancelled,
}

/// <summary>Local, durable timeout. Only the one matching the transfer's current sequence applies.</summary>
public sealed record TransferTimedOut(Guid TransferId, int Sequence, TimeSpan Delay) : TimeoutMessage(Delay), ICorrelatedByTransfer;

public sealed record SagaTimeouts
{
    public const string Section = "SagaTimeouts";

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan AccountsReply { get; init; }

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan PaymentsReply { get; init; }

    public TimeSpan For(TransferState state) =>
        state is TransferState.SubmittingPayment or TransferState.CancellingPayment ? PaymentsReply : AccountsReply;
}

/// <summary>
/// The transfer row is also its Wolverine saga. Transitions live in TransferStateMachine.cs.
/// MarkCompleted is never called: finished rows are the business record.
/// </summary>
public sealed partial class Transfer : Saga
{
    private const int StuckAfterAttempts = 5;

    public Guid Id { get; set; }
    public Guid SourceAccountId { get; set; }
    public string Beneficiary { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public TransferState State { get; set; }
    public DateTimeOffset? TimeoutAt { get; set; }
    public int StepAttempts { get; set; }
    public TransferFailureReason? FailureReason { get; set; }
    public string? BankReference { get; set; }
    public int TimeoutSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsTerminal => State is TransferState.Completed or TransferState.Failed;

    // Not called Start: Wolverine would treat that as a start handler.
    public static (Transfer Transfer, ReserveFunds Command) Create(CreateTransferRequest request, DateTimeOffset now, SagaTimeouts timeouts)
    {
        var transfer = new Transfer
        {
            // Validation already ran, so the required fields are set.
            Id = request.Id!.Value,
            SourceAccountId = request.SourceAccountId!.Value,
            Beneficiary = request.Beneficiary!,
            Amount = request.Amount,
            Currency = request.Currency!,
            State = TransferState.ReservingFunds,
            CreatedAt = now,
        };
        transfer.Enter(now, timeouts);
        return (transfer, new ReserveFunds(transfer.Id, transfer.SourceAccountId, transfer.Amount));
    }

    public bool Matches(CreateTransferRequest request) =>
        SourceAccountId == request.SourceAccountId && Beneficiary == request.Beneficiary
        && Amount == request.Amount && Currency == request.Currency;

    public TransferTimedOut CurrentTimeout(SagaTimeouts timeouts) => new(Id, TimeoutSequence, timeouts.For(State));

    /// <summary>Applies a reply or timeout and returns the next command, if any.</summary>
    public ICorrelatedByTransfer? Apply(ICorrelatedByTransfer input, DateTimeOffset now, SagaTimeouts timeouts)
    {
        if (input.TransferId != Id || input is TransferTimedOut { Sequence: var sequence } && sequence != TimeoutSequence)
            return null;

        ICorrelatedByTransfer? command = null;
        new TransferStateMachine(this, now, timeouts, next => command = next).Fire(input);
        return command;
    }

    // Wolverine handlers. The saga, its outgoing messages and the inbox marker commit in one transaction.

    public OutgoingMessages Handle(FundsReserved reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(FundsReservationFailed reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(FundsReleased reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(FundsSettled reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(PaymentAccepted reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(PaymentRejected reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(PaymentCancelled reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger) =>
        ApplyReply(reply, envelope, clock, timeouts, logger);

    public OutgoingMessages Handle(TransferTimedOut timeout, TimeProvider clock, SagaTimeouts timeouts, ILogger<Transfer> logger)
    {
        var before = State;
        var sequence = TimeoutSequence;
        var command = Apply(timeout, clock.GetUtcNow(), timeouts);
        if (TimeoutSequence == sequence)
        {
            LogTimeoutIgnored(logger, Id, timeout.Sequence, State);
            return [];
        }
        if (StepAttempts >= StuckAfterAttempts)
            LogStuck(logger, Id, State, StepAttempts);
        else
            LogTimeoutFired(logger, Id, before, State, StepAttempts);
        return MessagesAfter(command, sequence, timeouts);
    }

    // A timeout is the only thing that moves a waiting transfer, so it is retried forever instead of dead-lettered.
    public static void Configure(HandlerChain chain)
    {
        if (chain.MessageType != typeof(TransferTimedOut))
            return;
        chain.OnAnyException()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250))
            .Then.ScheduleRetryIndefinitely(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    private OutgoingMessages ApplyReply(ICorrelatedByTransfer reply, Envelope envelope, TimeProvider clock, SagaTimeouts timeouts, ILogger logger)
    {
        var replyType = reply.GetType().Name;
        var before = State;
        var sequence = TimeoutSequence;
        var command = Apply(reply, clock.GetUtcNow(), timeouts);
        if (before != State)
            LogApplied(logger, Id, envelope.Id, replyType, before, State, "transition");
        else if (ContradictsCommittedOutcome(reply, before))
            LogContradiction(logger, Id, envelope.Id, replyType, before);
        else
            LogApplied(logger, Id, envelope.Id, replyType, before, State, "ignored");
        return MessagesAfter(command, sequence, timeouts);
    }

    private OutgoingMessages MessagesAfter(ICorrelatedByTransfer? command, int previousSequence, SagaTimeouts timeouts)
    {
        var messages = new OutgoingMessages();
        if (command is not null)
            messages.Add(command);
        if (TimeoutSequence != previousSequence)
            messages.Add(CurrentTimeout(timeouts));
        return messages;
    }

    // Money moved the other way from the outcome this transfer already committed to.
    private static bool ContradictsCommittedOutcome(ICorrelatedByTransfer reply, TransferState state) => (reply, state) switch
    {
        (PaymentAccepted or FundsSettled, TransferState.ReleasingFunds or TransferState.Failed) => true,
        (FundsReleased or PaymentRejected or PaymentCancelled, TransferState.SettlingFunds or TransferState.Completed) => true,
        _ => false,
    };

    private static TransferFailureReason ToFailureReason(ReservationFailureReason reason) => reason switch
    {
        ReservationFailureReason.InsufficientFunds => TransferFailureReason.InsufficientFunds,
        ReservationFailureReason.AccountNotFound => TransferFailureReason.AccountNotFound,
        _ => TransferFailureReason.ReleasedBeforeReserved,
    };

    private static TransferFailureReason ToFailureReason(PaymentRejectionReason _) => TransferFailureReason.BeneficiaryRejected;

    // Entering a state starts a fresh step with its own timeout.
    private void Enter(DateTimeOffset now, SagaTimeouts timeouts)
    {
        StepAttempts = 0;
        if (IsTerminal)
        {
            TimeoutAt = null;
            UpdatedAt = now;
            return;
        }
        ScheduleNextTimeout(now, timeouts);
    }

    private void Retry(DateTimeOffset now, SagaTimeouts timeouts)
    {
        StepAttempts++;
        ScheduleNextTimeout(now, timeouts);
    }

    private void ScheduleNextTimeout(DateTimeOffset now, SagaTimeouts timeouts)
    {
        TimeoutAt = now + timeouts.For(State);
        UpdatedAt = now;
        TimeoutSequence++;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Transfer {TransferId}: {MessageType} {MessageId} {PreviousState} -> {State} ({Disposition})")]
    private static partial void LogApplied(ILogger logger, Guid transferId, Guid messageId, string messageType,
        TransferState previousState, TransferState state, string disposition);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Transfer {TransferId}: {MessageType} {MessageId} contradicts state {State}, ignored")]
    private static partial void LogContradiction(ILogger logger, Guid transferId, Guid messageId, string messageType, TransferState state);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Transfer {TransferId}: timed out, {PreviousState} -> {State} (attempt {StepAttempts})")]
    private static partial void LogTimeoutFired(ILogger logger, Guid transferId, TransferState previousState, TransferState state, int stepAttempts);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Error,
        Message = "Transfer {TransferId}: stuck in {State} after {StepAttempts} resends")]
    private static partial void LogStuck(ILogger logger, Guid transferId, TransferState state, int stepAttempts);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Transfer {TransferId}: timeout {Sequence} ignored in {State}")]
    private static partial void LogTimeoutIgnored(ILogger logger, Guid transferId, int sequence, TransferState state);
}
