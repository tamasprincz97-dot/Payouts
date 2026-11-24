using Payouts.Contracts;
using Payouts.Transfers;
using Xunit;

namespace Payouts.Tests;

// The saga's state machine without hosts or Docker.
public sealed class TransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly SagaTimeouts Timeouts = new() { AccountsReply = TimeSpan.FromSeconds(10), PaymentsReply = TimeSpan.FromSeconds(15) };
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public void Approved_payment_settles_and_completes()
    {
        var (transfer, reserve) = NewTransfer();
        Assert.Equal(new ReserveFunds(transfer.Id, AccountId, 25m), reserve);

        Assert.Equal(new SubmitPayment(transfer.Id, "DEMOAPPROVE", 25m), transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts));
        Assert.Equal(new SettleFunds(transfer.Id), transfer.Apply(new PaymentAccepted(transfer.Id, "SIM-1"), Now, Timeouts));
        Assert.Null(transfer.Apply(new FundsSettled(transfer.Id), Now, Timeouts));

        Assert.Equal(TransferState.Completed, transfer.State);
        Assert.Equal("SIM-1", transfer.BankReference);
        Assert.Null(transfer.TimeoutAt);
    }

    [Fact]
    public void Failed_reservation_fails_without_compensation()
    {
        var (transfer, _) = NewTransfer();

        var next = transfer.Apply(new FundsReservationFailed(transfer.Id, ReservationFailureReason.InsufficientFunds), Now, Timeouts);

        Assert.Null(next);
        Assert.Equal((TransferState.Failed, TransferFailureReason.InsufficientFunds), (transfer.State, transfer.FailureReason));
    }

    [Fact]
    public void Rejected_payment_releases_funds()
    {
        var (transfer, _) = NewTransfer();
        transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts);

        Assert.Equal(new ReleaseFunds(transfer.Id),
            transfer.Apply(new PaymentRejected(transfer.Id, PaymentRejectionReason.BeneficiaryRejected), Now, Timeouts));
        transfer.Apply(new FundsReleased(transfer.Id), Now, Timeouts);

        Assert.Equal((TransferState.Failed, TransferFailureReason.BeneficiaryRejected), (transfer.State, transfer.FailureReason));
    }

    [Fact]
    public void Payment_timeout_cancels_before_releasing_funds()
    {
        var (transfer, _) = NewTransfer();
        transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts);

        Assert.Equal(new CancelPayment(transfer.Id), transfer.Apply(transfer.CurrentTimeout(Timeouts), Now, Timeouts));
        Assert.Equal(TransferState.CancellingPayment, transfer.State);
        Assert.Equal(new ReleaseFunds(transfer.Id), transfer.Apply(new PaymentCancelled(transfer.Id), Now, Timeouts));
        transfer.Apply(new FundsReleased(transfer.Id), Now, Timeouts);

        Assert.Equal((TransferState.Failed, TransferFailureReason.PaymentCancelled), (transfer.State, transfer.FailureReason));
    }

    [Fact]
    public void Payment_accepted_while_cancelling_still_settles()
    {
        var (transfer, _) = NewTransfer();
        transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts);
        transfer.Apply(transfer.CurrentTimeout(Timeouts), Now, Timeouts);

        Assert.Equal(new SettleFunds(transfer.Id), transfer.Apply(new PaymentAccepted(transfer.Id, "SIM-1"), Now, Timeouts));
        Assert.Equal(TransferState.SettlingFunds, transfer.State);
    }

    [Fact]
    public void Stale_and_late_inputs_are_ignored()
    {
        var (transfer, _) = NewTransfer();
        var staleTimeout = transfer.CurrentTimeout(Timeouts);
        transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts);

        Assert.Null(transfer.Apply(staleTimeout, Now, Timeouts));
        Assert.Null(transfer.Apply(new FundsReserved(transfer.Id), Now, Timeouts));
        Assert.Equal(TransferState.SubmittingPayment, transfer.State);

        transfer.Apply(new PaymentAccepted(transfer.Id, "SIM-1"), Now, Timeouts);
        transfer.Apply(new FundsSettled(transfer.Id), Now, Timeouts);
        Assert.Null(transfer.Apply(new PaymentRejected(transfer.Id, PaymentRejectionReason.BeneficiaryRejected), Now, Timeouts));
        Assert.Equal(TransferState.Completed, transfer.State);
    }

    private static (Transfer, ReserveFunds) NewTransfer() =>
        Transfer.Create(new CreateTransferRequest(Guid.NewGuid(), AccountId, "DEMOAPPROVE", 25m, "EUR"), Now, Timeouts);
}
