using Microsoft.EntityFrameworkCore;

namespace Payouts.Transfers;

public sealed class TransfersDbContext(DbContextOptions<TransfersDbContext> options) : DbContext(options)
{
    public DbSet<Transfer> Transfers => Set<Transfer>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Transfer>(e =>
    {
        e.ToTable("transfers", table =>
        {
            table.HasCheckConstraint("ck_transfers_amount", "amount > 0");
            table.HasCheckConstraint("ck_transfers_currency", "currency = 'EUR'");
        });
        e.HasKey(t => t.Id).HasName("pk_transfers");
        e.Property(t => t.Id).ValueGeneratedNever();
        e.Property(t => t.Amount).HasPrecision(18, 2);
        e.Property(t => t.Currency).HasColumnType("character(3)");
        e.Property(t => t.State).HasConversion<string>();
        e.Property(t => t.FailureReason).HasConversion<string>();
        // Wolverine bumps Version on every save, so a lost race becomes a retry.
        e.Property(t => t.Version).IsConcurrencyToken();
        e.Ignore(t => t.IsTerminal);
    });
}
