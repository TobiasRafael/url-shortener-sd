var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The Event Hubs click consumer is registered here in the analytics stage.

var host = builder.Build();
host.Run();
