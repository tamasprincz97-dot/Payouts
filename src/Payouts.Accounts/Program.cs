using Payouts.Accounts;
using Payouts.Messaging;

var app = AccountsApp.Build(args);
await app.PrepareDatabaseAsync();
await app.RunAsync();

namespace Payouts.Accounts
{
    public static class AccountsApp
    {
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ApplicationName = typeof(AccountsApp).Assembly.GetName().Name,
            });
            configure?.Invoke(builder);
            builder.UsePayoutsMessaging<AccountsDbContext>(typeof(FundsHandler));
            var app = builder.Build();
            app.MapHealthChecks("/health");
            return app;
        }

        public static async Task PrepareDatabaseAsync(this WebApplication app, CancellationToken ct = default)
        {
            await app.EnsureServiceSchemaAsync<AccountsDbContext>(ct);
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().SeedAsync(ct);
        }
    }
}
