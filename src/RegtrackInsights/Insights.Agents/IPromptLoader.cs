namespace Insights.Agents;

/// <summary>Prompts are files under Agents:PromptDirectory, never string literals in code (docs/CONFIGURATION.md).</summary>
public interface IPromptLoader
{
    Task<string> LoadAsync(string fileName, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPromptLoader"/>
public sealed class FilePromptLoader(string promptDirectory) : IPromptLoader
{
    public Task<string> LoadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var root = Path.IsPathRooted(promptDirectory) ? promptDirectory : Path.Combine(AppContext.BaseDirectory, promptDirectory);
        return File.ReadAllTextAsync(Path.Combine(root, fileName), cancellationToken);
    }
}
