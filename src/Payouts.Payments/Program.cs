using Payouts.Messaging;
using Payouts.Payments;

var app = PaymentsApp.Build(args);
await app.EnsureServiceSchemaAsync<PaymentsDbContext>();
await app.RunAsync();

namespace Payouts.Payments
{
    public static class PaymentsApp
    {
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ApplicationName = typeof(PaymentsApp).Assembly.GetName().Name,
            });
            configure?.Invoke(builder);
            builder.UsePayoutsMessaging<PaymentsDbContext>(typeof(PaymentHandler));
            var app = builder.Build();
            app.MapHealthChecks("/health");
            return app;
        }
    }
}
