using Microsoft.Agents.AI;

namespace Insights.Agents;

/// <summary>
/// The patch LLM call. [CHANGED 2026-10-09] Takes the SCOPED request JSON built by
/// ScopedPatchSplicer.BuildPlan (only the failing cards plus page CSS/JS as context) and returns the
/// model's raw JSON answer ({cards:[{ordinal,html}], unfixable:[...]}); the activity validates and
/// splices it. It no longer receives or returns the whole page - that contract lost the data
/// block, the font block and a script-addressed container on real reports (see
/// ScopedPatchSplicer's own doc comment). Prompt: prompts/10_patch_render_defect_v2.md.
/// </summary>
public interface IPatchRenderAgent
{
    Task<AgentCallResult<string>> PatchAsync(string scopedRequestJson, CancellationToken cancellationToken = default);
}

public sealed class MafPatchRenderAgent(AIAgent agent) : IPatchRenderAgent
{
    public async Task<AgentCallResult<string>> PatchAsync(string scopedRequestJson, CancellationToken cancellationToken = default)
    {
        var response = await agent.RunAsync(
            "Fix only the named problem(s) in the cards below. Respond with the JSON contract from your instructions.\n" + scopedRequestJson,
            cancellationToken: cancellationToken);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Patch render agent returned no text.");
        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<string>(text.Trim(), totalTokens);
    }
}
