using Microsoft.EntityFrameworkCore;
using Payouts.Contracts;

namespace Payouts.Accounts;

public sealed class Account
{
    public Guid Id { get; set; }
    public decimal Available { get; set; }
}

public enum ReservationStatus { Reserved, Settled, Released, Rejected }

/// <summary>One row per transfer, so a transfer reserves at most once and a replayed command gets the same answer.</summary>
public sealed class Reservation
{
    public Guid TransferId { get; set; }
    public Guid? AccountId { get; set; }
    public decimal Amount { get; set; }
    public ReservationStatus Status { get; set; }
    public ReservationFailureReason? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void MoveTo(ReservationStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public static readonly Guid RichAccount = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid PoorAccount = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.ToTable("accounts", table => table.HasCheckConstraint("ck_accounts_available", "available >= 0"));
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Available).HasPrecision(18, 2);
        });
        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations");
            e.HasKey(r => r.TransferId).HasName("pk_reservations");
            e.Property(r => r.TransferId).ValueGeneratedNever();
            e.Property(r => r.Amount).HasPrecision(18, 2);
            // Every transition changes the status, so a concurrent save fails and is retried.
            e.Property(r => r.Status).HasConversion<string>().IsConcurrencyToken();
            e.Property(r => r.Reason).HasConversion<string>();
            e.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId);
        });
    }

    /// <summary>Adds the demo accounts if they are missing.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        Account[] demo = [new() { Id = RichAccount, Available = 1000m }, new() { Id = PoorAccount, Available = 50m }];
        var existing = await Accounts.Select(a => a.Id).ToListAsync(ct);
        Accounts.AddRange(demo.Where(a => !existing.Contains(a.Id)));
        await SaveChangesAsync(ct);
    }
}
