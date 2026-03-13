using System;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RepairPlanner;

public class Program
{
    public static async Task Main(string[] args)
    {
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Add options
        services.AddOptions<CosmosDbOptions>()
            .Configure(options =>
            {
                options.Endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? throw new InvalidOperationException("COSMOS_ENDPOINT not set");
                options.Key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? throw new InvalidOperationException("COSMOS_KEY not set");
                options.DatabaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? throw new InvalidOperationException("COSMOS_DATABASE_NAME not set");
            });

        // Add services
        services.AddSingleton<AIProjectClient>(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT") ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT not set");
            return new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
        });
        services.AddSingleton<CosmosDbService>();
        services.AddSingleton<IFaultMappingService, FaultMappingService>();
        services.AddSingleton<RepairPlannerAgent>();

        await using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<Program>>();
        var agent = provider.GetRequiredService<RepairPlannerAgent>();

        try
        {
            // Ensure agent is registered
            await agent.EnsureAgentVersionAsync();

            // Create sample fault
            var sampleFault = new DiagnosedFault
            {
                Id = Guid.NewGuid().ToString(),
                FaultType = "curing_temperature_excessive",
                MachineId = "TIRE-CURING-001",
                Description = "Curing temperature exceeded safe limits, causing potential material degradation.",
                Timestamp = DateTime.UtcNow
            };

            logger.LogInformation("Processing fault: {FaultType} on machine {MachineId}", sampleFault.FaultType, sampleFault.MachineId);

            // Run repair planning
            var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

            logger.LogInformation("Work order created: {Id} - {Title}", workOrder.Id, workOrder.Title);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in repair planning workflow");
        }
    }
}
