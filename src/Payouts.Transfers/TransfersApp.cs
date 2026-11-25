using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Payouts.Messaging;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Payouts.Transfers;

public sealed record CreateTransferRequest(
    [Required] Guid? Id,
    [Required] Guid? SourceAccountId,
    [Required, AllowedValues("DEMOAPPROVE", "DEMOREJECT", "DEMOTIMEOUT")] string? Beneficiary,
    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal Amount,
    [Required, AllowedValues("EUR")] string? Currency) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        // An empty ID can't correlate saga replies, and sub-cent amounts round differently in each database.
        if (Id == Guid.Empty)
            yield return new("The Id field must not be empty.", [nameof(Id)]);
        if (SourceAccountId == Guid.Empty)
            yield return new("The SourceAccountId field must not be empty.", [nameof(SourceAccountId)]);
        if (decimal.Round(Amount, 2) != Amount)
            yield return new("The Amount field must be in whole cents.", [nameof(Amount)]);
    }
}

public sealed record TransferView(Guid Id, string State, string Outcome, DateTimeOffset? TimeoutAt, int StepAttempts,
    string? FailureReason, string? BankReference, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TransferView From(Transfer t) => new(t.Id, t.State.ToString(), t.IsTerminal ? t.State.ToString() : "Pending",
        t.TimeoutAt, t.StepAttempts, t.State == TransferState.Failed ? t.FailureReason?.ToString() : null, t.BankReference,
        t.CreatedAt, t.UpdatedAt);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateTransferRequest))]
[JsonSerializable(typeof(TransferView))]
[JsonSerializable(typeof(TransferTimedOut))]
public sealed partial class TransfersJsonContext : JsonSerializerContext;

public static partial class TransfersApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(TransfersApp).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);
        builder.UsePayoutsMessaging<TransfersDbContext>(typeof(Transfer), TransfersJsonContext.Default);

        builder.Services.AddProblemDetails();
        builder.Services.AddValidation();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, TransfersJsonContext.Default);
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict; // no "25.00" strings for money
        });
        builder.Services.AddOptions<SagaTimeouts>()
            .BindConfiguration(SagaTimeouts.Section)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<SagaTimeouts>>().Value);

        var app = builder.Build();
        app.MapHealthChecks("/health");
        app.MapPost("/transfers", CreateAsync);
        app.MapGet("/transfers/{id:guid}", GetAsync);
        return app;
    }

    // Idempotent by transfer ID: 202 for a new transfer, 200 for a repeat, 409 for a different request with the same ID.
    private static async Task<IResult> CreateAsync(CreateTransferRequest request, IDbContextOutbox<TransfersDbContext> outbox,
        IServiceScopeFactory scopes, TimeProvider clock, SagaTimeouts timeouts, HttpResponse response, ILogger<Transfer> logger,
        CancellationToken ct)
    {
        if (await FindAsync(outbox.DbContext, request.Id, ct) is { } existing)
            return Replay(existing, request, response);

        // The row, ReserveFunds and the first timeout commit together.
        var (transfer, reserve) = Transfer.Create(request, clock.GetUtcNow(), timeouts);
        outbox.DbContext.Transfers.Add(transfer);
        var timeout = transfer.CurrentTimeout(timeouts);
        await outbox.SendAsync(reserve, new DeliveryOptions { CorrelationId = transfer.Id.ToString() });
        await outbox.ScheduleAsync(timeout, timeout.Delay, new DeliveryOptions { CorrelationId = transfer.Id.ToString() });
        try
        {
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_transfers" })
        {
            // A concurrent POST with the same ID won. Read its row in a fresh context and answer like a replay.
            await using var scope = scopes.CreateAsyncScope();
            var winner = await FindAsync(scope.ServiceProvider.GetRequiredService<TransfersDbContext>(), request.Id, ct);
            return Replay(winner!, request, response);
        }

        LogCreated(logger, transfer.Id);
        return TypedResults.Accepted($"/transfers/{transfer.Id}", TransferView.From(transfer));
    }

    private static Task<Transfer?> FindAsync(TransfersDbContext db, Guid? id, CancellationToken ct) =>
        db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);

    private static IResult Replay(Transfer existing, CreateTransferRequest request, HttpResponse response)
    {
        if (!existing.Matches(request))
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The transfer ID already belongs to a different request.");
        response.Headers.Location = $"/transfers/{existing.Id}";
        return TypedResults.Ok(TransferView.From(existing));
    }

    private static async Task<IResult> GetAsync(Guid id, TransfersDbContext db, CancellationToken ct) =>
        await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) is { } transfer
            ? TypedResults.Ok(TransferView.From(transfer))
            : TypedResults.NotFound();

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Transfer {TransferId}: created")]
    private static partial void LogCreated(ILogger logger, Guid transferId);
}
