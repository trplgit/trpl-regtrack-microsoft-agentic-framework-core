using System.Text.Json;
using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-28] Turns a tenant-memory section's OLDER run entries into one summary block when
/// the section outgrows its size limit - so older months are summarised, not cut. The Keep block
/// and the two newest runs never reach this: TenantMemoryTool only hands over the older entries,
/// and checks the returned block (TenantMemoryCompactor.IsValidSummary) before storing it. Null or
/// an invalid block means the caller falls back to the deterministic compaction.
/// </summary>
public interface ITenantMemorySummarizer
{
    Task<string?> SummarizeAsync(string dimensionName, string olderEntries, int maxChars, CancellationToken cancellationToken = default);
}

public sealed class MafTenantMemorySummarizer(AIAgent agent) : ITenantMemorySummarizer
{
    public async Task<string?> SummarizeAsync(string dimensionName, string olderEntries, int maxChars, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            dimension_name = dimensionName,
            max_characters = maxChars,
            older_entries = olderEntries,
        });
        var response = await agent.RunAsync("Summarise these older tenant-memory entries:\n" + payload, cancellationToken: cancellationToken);
        return response.Text?.Trim();
    }
}
