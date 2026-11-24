using Payouts.Messaging;
using Payouts.Transfers;

var app = TransfersApp.Build(args);
await app.EnsureServiceSchemaAsync<TransfersDbContext>();
await app.RunAsync();
