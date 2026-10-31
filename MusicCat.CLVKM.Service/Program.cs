using MusicCat.CLVKM.Service;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "KeyGlowMaster RGB Service";
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
