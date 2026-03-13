using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RepairPlanner;

public sealed class CosmosDbService
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly ILogger<CosmosDbService> _logger;

    private const string TechniciansContainer = "Technicians";
    private const string PartsContainer = "PartsInventory";
    private const string WorkOrdersContainer = "WorkOrders";

    public CosmosDbService(IOptions<CosmosDbOptions> options, ILogger<CosmosDbService> logger)
    {
        _cosmosClient = new CosmosClient(options.Value.Endpoint, options.Value.Key);
        _databaseName = options.Value.DatabaseName;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Technician>> GetAvailableTechniciansAsync(IReadOnlyList<string> requiredSkills, CancellationToken ct = default)
    {
        try
        {
            var container = _cosmosClient.GetContainer(_databaseName, TechniciansContainer);
            // Simplified query: find technicians with at least one matching skill, then filter in code for all
            var query = new QueryDefinition("SELECT * FROM c WHERE c.isAvailable = true");
            var iterator = container.GetItemQueryIterator<Technician>(query);
            var technicians = new List<Technician>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                technicians.AddRange(response);
            }
            var qualified = technicians.Where(t => requiredSkills.All(skill => t.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase))).ToList();
            _logger.LogInformation("Found {Count} qualified technicians for skills: {Skills}", qualified.Count, string.Join(", ", requiredSkills));
            return qualified;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying technicians for skills: {Skills}", string.Join(", ", requiredSkills));
            return Array.Empty<Technician>();
        }
    }

    public async Task<IReadOnlyList<Part>> GetPartsByNumbersAsync(IReadOnlyList<string> partNumbers, CancellationToken ct = default)
    {
        try
        {
            var container = _cosmosClient.GetContainer(_databaseName, PartsContainer);
            var parts = new List<Part>();
            foreach (var partNumber in partNumbers)
            {
                var query = new QueryDefinition("SELECT * FROM c WHERE c.partNumber = @partNumber")
                    .WithParameter("@partNumber", partNumber);
                var iterator = container.GetItemQueryIterator<Part>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync(ct);
                    parts.AddRange(response);
                }
            }
            _logger.LogInformation("Fetched {Count} parts for numbers: {Numbers}", parts.Count, string.Join(", ", partNumbers));
            return parts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching parts for numbers: {Numbers}", string.Join(", ", partNumbers));
            return Array.Empty<Part>();
        }
    }

    public async Task CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        try
        {
            var container = _cosmosClient.GetContainer(_databaseName, WorkOrdersContainer);
            workOrder.Id ??= Guid.NewGuid().ToString();
            await container.UpsertItemAsync(workOrder, new PartitionKey(workOrder.Status), cancellationToken: ct);
            _logger.LogInformation("Created work order {Id}", workOrder.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating work order {Id}", workOrder.Id);
            throw;
        }
    }
}