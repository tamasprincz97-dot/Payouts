using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization.Metadata;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Payouts.Contracts;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace Payouts.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    [Required] public string Host { get; init; } = "localhost";
    [Range(1, 65535)] public int Port { get; init; } = 5672;
    [Required] public string User { get; init; } = "guest";
    [Required] public string Password { get; init; } = "guest";
    [Required] public string VirtualHost { get; init; } = "/";
}

/// <summary>Wolverine setup shared by the three services: RabbitMQ, PostgreSQL inbox/outbox, EF Core transactions.</summary>
public static class MessagingSetup
{
    public static void UsePayoutsMessaging<TContext>(this WebApplicationBuilder builder, Type handlerType,
        IJsonTypeInfoResolver? localMessages = null)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString("ServiceDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:ServiceDb is required.");
        var rabbit = builder.Configuration.GetSection(RabbitMqOptions.Section).Get<RabbitMqOptions>() ?? new();
        Validator.ValidateObject(rabbit, new ValidationContext(rabbit), validateAllProperties: true);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddDbContextWithWolverineIntegration<TContext>((services, options) =>
            options.UseNpgsql(connectionString).AddInterceptors(services.GetServices<IInterceptor>()));
        builder.Services.AddHealthChecks().AddDbContextCheck<TContext>();

        builder.Host.UseWolverine(options =>
        {
            options.ApplicationAssembly = handlerType.Assembly;
            options.ServiceName = builder.Environment.ApplicationName;
            options.Discovery.DisableConventionalDiscovery();
            options.Discovery.IncludeType(handlerType);
            options.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;

            options.Durability.Mode = DurabilityMode.Solo; // one node per service
            options.Durability.KeepAfterMessageHandling = TimeSpan.FromDays(1);
            options.Durability.ScheduledJobPollingTime = TimeSpan.FromSeconds(1);
            options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;
            options.PersistMessagesWithPostgresql(connectionString, "wolverine");
            options.UseEntityFrameworkCoreTransactions();
            options.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);
            options.Policies.UseDurableLocalQueues();

            options.UseSystemTextJsonForSerialization(json =>
            {
                json.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
                if (localMessages is not null)
                    json.TypeInfoResolverChain.Insert(1, localMessages);
                if (!json.TypeInfoResolverChain.OfType<DefaultJsonTypeInfoResolver>().Any())
                    json.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
            });

            ConfigureRabbitMq(options, rabbit);

            // Lost concurrency races and transient database errors pass on a fresh attempt.
            options.OnException<Exception>()
                .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1))
                .Then.MoveToErrorQueue();

            options.Policies.ForMessagesOfType<ICorrelatedByTransfer>().Audit(message => message.TransferId);
            options.Policies.MessageSuccessLogLevel(LogLevel.Debug);
        });
    }

    public static async Task EnsureServiceSchemaAsync<TContext>(this WebApplication app, CancellationToken ct = default)
        where TContext : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync(ct);
    }

    private static void ConfigureRabbitMq(WolverineOptions options, RabbitMqOptions rabbit)
    {
        options.UseRabbitMq(connection =>
            {
                connection.HostName = rabbit.Host;
                connection.Port = rabbit.Port;
                connection.UserName = rabbit.User;
                connection.Password = rabbit.Password;
                connection.VirtualHost = rabbit.VirtualHost;
            })
            .AutoProvision()
            .ConfigureChannelCreation(channel =>
            {
                channel.PublisherConfirmationsEnabled = true;
                channel.PublisherConfirmationTrackingEnabled = true;
            })
            .UseConventionalRouting(convention => convention
                .IncludeTypes(MessageQueues.IsContract)
                .QueueNameForListener(MessageQueues.NameFor)
                .ConfigureListeners((listener, context) => listener
                    .DeadLetterQueueing(new DeadLetterQueue(MessageQueues.ErrorQueueFor(MessageQueues.NameFor(context.MessageType))))
                    .UseDurableInbox()));

        // Publish straight to the queue so a command sent before its consumer starts still waits there.
        // Every host must declare the queue with the same dead-letter setting or RabbitMQ refuses it.
        foreach (var type in MessageQueues.Contracts)
        {
            var queue = MessageQueues.NameFor(type);
            options.PublishMessage(type).ToRabbitQueue(queue)
                .DeadLetterQueueing(new DeadLetterQueue(MessageQueues.ErrorQueueFor(queue)))
                .UseDurableOutbox();
        }
    }
}
