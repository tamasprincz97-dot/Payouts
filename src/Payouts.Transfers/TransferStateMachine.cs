using Payouts.Contracts;
using Stateless;

namespace Payouts.Transfers;

public sealed partial class Transfer
{
    // Built per Apply call on top of Transfer.State. Unexpected inputs (late, duplicate) are ignored.
    private sealed class TransferStateMachine
    {
        private enum Trigger
        {
            FundsReserved, ReservationFailed, FundsReleased, FundsSettled,
            PaymentAccepted, PaymentRejected, PaymentCancelled, TimedOut,
        }

        private readonly StateMachine<TransferState, Trigger> machine;
        private readonly StateMachine<TransferState, Trigger>.TriggerWithParameters<TransferFailureReason> reservationFailed;
        private readonly StateMachine<TransferState, Trigger>.TriggerWithParameters<string> paymentAccepted;
        private readonly StateMachine<TransferState, Trigger>.TriggerWithParameters<TransferFailureReason> paymentRejected;

        public TransferStateMachine(Transfer transfer, DateTimeOffset now, SagaTimeouts timeouts, Action<ICorrelatedByTransfer> send)
        {
            machine = new StateMachine<TransferState, Trigger>(() => transfer.State, state => transfer.State = state);
            reservationFailed = machine.SetTriggerParameters<TransferFailureReason>(Trigger.ReservationFailed);
            paymentAccepted = machine.SetTriggerParameters<string>(Trigger.PaymentAccepted);
            paymentRejected = machine.SetTriggerParameters<TransferFailureReason>(Trigger.PaymentRejected);
            machine.OnUnhandledTrigger((_, _) => { });

            void Enter() => transfer.Enter(now, timeouts);
            void Resend(ICorrelatedByTransfer command)
            {
                transfer.Retry(now, timeouts);
                send(command);
            }

            machine.Configure(TransferState.ReservingFunds)
                .Permit(Trigger.FundsReserved, TransferState.SubmittingPayment)
                .Permit(Trigger.ReservationFailed, TransferState.Failed)
                // The reserve may have committed without its reply arriving, so release rather than fail outright.
                .Permit(Trigger.TimedOut, TransferState.ReleasingFunds);

            machine.Configure(TransferState.SubmittingPayment)
                .OnEntry(Enter)
                .OnEntry(() => send(new SubmitPayment(transfer.Id, transfer.Beneficiary, transfer.Amount)))
                .Permit(Trigger.PaymentAccepted, TransferState.SettlingFunds)
                .Permit(Trigger.PaymentRejected, TransferState.ReleasingFunds)
                // Pivot: never release funds before Payments has committed a rejection or cancellation.
                .Permit(Trigger.TimedOut, TransferState.CancellingPayment);

            // The bank may still decide while the cancel is in flight; its first decision wins.
            machine.Configure(TransferState.CancellingPayment)
                .OnEntry(Enter)
                .OnEntry(() => send(new CancelPayment(transfer.Id)))
                .Permit(Trigger.PaymentAccepted, TransferState.SettlingFunds)
                .Permit(Trigger.PaymentRejected, TransferState.ReleasingFunds)
                .Permit(Trigger.PaymentCancelled, TransferState.ReleasingFunds)
                .InternalTransition(Trigger.TimedOut, () => Resend(new CancelPayment(transfer.Id)));

            machine.Configure(TransferState.SettlingFunds)
                .OnEntryFrom(paymentAccepted, reference => transfer.BankReference = reference)
                .OnEntry(Enter)
                .OnEntry(() => send(new SettleFunds(transfer.Id)))
                .Permit(Trigger.FundsSettled, TransferState.Completed)
                .InternalTransition(Trigger.TimedOut, () => Resend(new SettleFunds(transfer.Id)));

            machine.Configure(TransferState.ReleasingFunds)
                .OnEntryFrom(Trigger.TimedOut, () => transfer.FailureReason = TransferFailureReason.ReservationTimedOut)
                .OnEntryFrom(paymentRejected, reason => transfer.FailureReason = reason)
                .OnEntryFrom(Trigger.PaymentCancelled, () => transfer.FailureReason = TransferFailureReason.PaymentCancelled)
                .OnEntry(Enter)
                .OnEntry(() => send(new ReleaseFunds(transfer.Id)))
                .Permit(Trigger.FundsReleased, TransferState.Failed)
                .InternalTransition(Trigger.TimedOut, () => Resend(new ReleaseFunds(transfer.Id)));

            machine.Configure(TransferState.Completed)
                .OnEntry(() => transfer.FailureReason = null)
                .OnEntry(Enter);

            machine.Configure(TransferState.Failed)
                .OnEntryFrom(reservationFailed, reason => transfer.FailureReason = reason)
                .OnEntry(Enter);
        }

        public void Fire(ICorrelatedByTransfer input)
        {
            switch (input)
            {
                case FundsReserved:
                    machine.Fire(Trigger.FundsReserved);
                    break;
                case FundsReservationFailed failed:
                    machine.Fire(reservationFailed, ToFailureReason(failed.Reason));
                    break;
                case FundsReleased:
                    machine.Fire(Trigger.FundsReleased);
                    break;
                case FundsSettled:
                    machine.Fire(Trigger.FundsSettled);
                    break;
                case PaymentAccepted accepted:
                    machine.Fire(paymentAccepted, accepted.BankReference);
                    break;
                case PaymentRejected rejected:
                    machine.Fire(paymentRejected, ToFailureReason(rejected.Reason));
                    break;
                case PaymentCancelled:
                    machine.Fire(Trigger.PaymentCancelled);
                    break;
                case TransferTimedOut:
                    machine.Fire(Trigger.TimedOut);
                    break;
            }
        }
    }
}
