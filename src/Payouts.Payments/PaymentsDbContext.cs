using Microsoft.EntityFrameworkCore;
using Payouts.Contracts;

namespace Payouts.Payments;

public enum PaymentStatus { Accepted, Rejected, Cancelled }

/// <summary>The write-once bank decision for one transfer.</summary>
public sealed class Payment
{
    public Guid TransferId { get; set; }
    public PaymentStatus Status { get; set; }
    public string? Beneficiary { get; set; }
    public decimal? Amount { get; set; }
    public string? BankReference { get; set; }
    public PaymentRejectionReason? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICorrelatedByTransfer ToReply() => Status switch
    {
        PaymentStatus.Accepted => new PaymentAccepted(TransferId, BankReference!),
        PaymentStatus.Rejected => new PaymentRejected(TransferId, Reason!.Value),
        _ => new PaymentCancelled(TransferId),
    };
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Payment>(e =>
    {
        e.ToTable("payments");
        e.HasKey(p => p.TransferId).HasName("pk_payments");
        e.Property(p => p.TransferId).ValueGeneratedNever();
        e.Property(p => p.Amount).HasPrecision(18, 2);
        e.Property(p => p.Status).HasConversion<string>();
        e.Property(p => p.Reason).HasConversion<string>();
    });
}
