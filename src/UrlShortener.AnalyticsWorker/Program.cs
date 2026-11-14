var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Storage first: its Cosmos initializer is a hosted service too, and hosted services start in
// registration order, so the schema exists before the first batch is written.
builder.AddUrlShortenerStorage();
builder.AddClickConsumer();

var host = builder.Build();
host.Run();
