using AlbionCompanion.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AlbionCompanionService");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
