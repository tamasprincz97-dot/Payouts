using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Payouts.Contracts;

public interface ICorrelatedByTransfer
{
    Guid TransferId { get; }
}

// Transfers -> Accounts
public sealed record ReserveFunds(Guid TransferId, Guid AccountId, decimal Amount) : ICorrelatedByTransfer;
public sealed record ReleaseFunds(Guid TransferId) : ICorrelatedByTransfer;
public sealed record SettleFunds(Guid TransferId) : ICorrelatedByTransfer;

// Accounts -> Transfers
public sealed record FundsReserved(Guid TransferId) : ICorrelatedByTransfer;
public sealed record FundsReservationFailed(Guid TransferId, ReservationFailureReason Reason) : ICorrelatedByTransfer;
public sealed record FundsReleased(Guid TransferId) : ICorrelatedByTransfer;
public sealed record FundsSettled(Guid TransferId) : ICorrelatedByTransfer;

// Transfers -> Payments
public sealed record SubmitPayment(Guid TransferId, string Beneficiary, decimal Amount) : ICorrelatedByTransfer;
public sealed record CancelPayment(Guid TransferId) : ICorrelatedByTransfer;

// Payments -> Transfers
public sealed record PaymentAccepted(Guid TransferId, string BankReference) : ICorrelatedByTransfer;
public sealed record PaymentRejected(Guid TransferId, PaymentRejectionReason Reason) : ICorrelatedByTransfer;
public sealed record PaymentCancelled(Guid TransferId) : ICorrelatedByTransfer;

[JsonConverter(typeof(JsonStringEnumConverter<ReservationFailureReason>))]
public enum ReservationFailureReason
{
    InsufficientFunds,
    AccountNotFound,
    ReleasedBeforeReserved, // the release arrived first
}

[JsonConverter(typeof(JsonStringEnumConverter<PaymentRejectionReason>))]
public enum PaymentRejectionReason
{
    BeneficiaryRejected,
}

/// <summary>One queue per message type: ReserveFunds goes to reserve-funds.</summary>
public static partial class MessageQueues
{
    public static IReadOnlyList<Type> Contracts { get; } =
        [.. typeof(ICorrelatedByTransfer).Assembly.GetTypes().Where(IsContract).OrderBy(t => t.Name, StringComparer.Ordinal)];

    public static bool IsContract(Type type) =>
        type.Assembly == typeof(ICorrelatedByTransfer).Assembly && type is { IsClass: true, IsAbstract: false }
        && typeof(ICorrelatedByTransfer).IsAssignableFrom(type);

    public static string NameFor(Type messageType) => KebabCase().Replace(messageType.Name, "-$1").ToLowerInvariant();

    public static string ErrorQueueFor(string queue) => queue + "-error";

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex KebabCase();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReserveFunds))]
[JsonSerializable(typeof(ReleaseFunds))]
[JsonSerializable(typeof(SettleFunds))]
[JsonSerializable(typeof(SubmitPayment))]
[JsonSerializable(typeof(CancelPayment))]
[JsonSerializable(typeof(FundsReserved))]
[JsonSerializable(typeof(FundsReservationFailed))]
[JsonSerializable(typeof(FundsReleased))]
[JsonSerializable(typeof(FundsSettled))]
[JsonSerializable(typeof(PaymentAccepted))]
[JsonSerializable(typeof(PaymentRejected))]
[JsonSerializable(typeof(PaymentCancelled))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
