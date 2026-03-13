using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace RepairPlanner;

public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.
        
        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]
        
        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.
        
        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        var definition = new PromptAgentDefinition(model: modelDeploymentName) { Instructions = AgentInstructions };
        await projectClient.Agents.CreateAgentVersionAsync(AgentName, new AgentVersionCreationOptions(definition), ct);
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        // 1. Get required skills and parts
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);

        // 2. Query technicians and parts
        var technicians = await cosmosDb.GetAvailableTechniciansAsync(requiredSkills, ct);
        var parts = await cosmosDb.GetPartsByNumbersAsync(requiredParts, ct);

        // 3. Select technician (first qualified)
        var assignedTechnician = technicians.FirstOrDefault();

        // 4. Build prompt
        var prompt = BuildPrompt(fault, requiredSkills, requiredParts, assignedTechnician, parts);

        // 5. Invoke agent
        var agent = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null, cancellationToken: ct);
        var resultJson = response.Text ?? "{}";

        // 6. Parse response
        var workOrder = JsonSerializer.Deserialize<WorkOrder>(resultJson, JsonOptions) ?? new WorkOrder();

        // 7. Apply defaults
        workOrder.Id ??= Guid.NewGuid().ToString();
        workOrder.WorkOrderNumber ??= $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8)}";
        workOrder.MachineId ??= fault.MachineId;
        workOrder.Type ??= "corrective";
        workOrder.Priority ??= "medium";
        workOrder.Status ??= "open";
        workOrder.AssignedTo ??= assignedTechnician?.Id;
        workOrder.EstimatedDuration = workOrder.EstimatedDuration == 0 ? 60 : workOrder.EstimatedDuration; // default 60 min

        // Ensure partsUsed have partId
        foreach (var partUsage in workOrder.PartsUsed)
        {
            var part = parts.FirstOrDefault(p => p.PartNumber == partUsage.PartNumber);
            partUsage.PartId ??= part?.Id;
        }

        // 8. Save to Cosmos DB
        await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        logger.LogInformation("Created work order {Id} for fault {FaultType}", workOrder.Id, fault.FaultType);
        return workOrder;
    }

    private static string BuildPrompt(DiagnosedFault fault, IReadOnlyList<string> skills, IReadOnlyList<string> parts, Technician? technician, IReadOnlyList<Part> availableParts)
    {
        var partsInfo = string.Join(", ", availableParts.Select(p => $"{p.PartNumber} ({p.Name})"));
        var technicianInfo = technician != null ? $"{technician.Name} (ID: {technician.Id})" : "None available";
        return $"""
            Diagnosed Fault:
            - Type: {fault.FaultType}
            - Machine: {fault.MachineId}
            - Description: {fault.Description}

            Required Skills: {string.Join(", ", skills)}
            Required Parts: {string.Join(", ", parts)}
            Available Parts: {partsInfo}
            Assigned Technician: {technicianInfo}

            Generate the work order JSON.
            """;
    }
}