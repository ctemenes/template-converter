using Converter.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Azure Functions host (isolated worker, .NET 8) for the same engine the API and CLI use.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
        services.AddSingleton(_ => new ConversionPipeline(LlmMapper.FromEnvironment())))
    .Build();

host.Run();
