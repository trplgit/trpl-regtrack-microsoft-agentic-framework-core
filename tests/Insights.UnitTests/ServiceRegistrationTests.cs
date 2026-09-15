using Insights.Data;
using Insights.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.UnitTests;

/// <summary>
/// Proves the container can actually BUILD what Program.cs asks for.
///
/// This is the only cheap check that means anything for DI work: a missing or mis-scoped
/// registration compiles perfectly and fails at RESOLVE time, in production, on the first run.
/// dotnet build will happily report "0 warnings" on a container that cannot construct itself.
///
/// No database, no LLM, no email - the connection string and API keys below are syntactically
/// valid placeholders and are never dialled, because nothing here executes a request.
/// </summary>
public sealed class ServiceRegistrationTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:RegTrack"] = "Server=localhost;Database=placeholder;Trusted_Connection=True;",
            ["Azure:BlobConnectionString"] = "UseDevelopmentStorage=true",
            ["Azure:BlobContainer"] = "insights-reports-temp",
            ["Azure:DigestBlobContainer"] = "insights-digests",
            // [MERGE FIX, 2026-09-11] AddInsightsFreeDigest now calls RegisterReportCodec
            // (WorkerRegistration.cs) - the digest artifact store shares the paid pipeline's
            // encrypt/blob-write registrations, which require these two. Placeholder, never
            // dialled, same reasoning as every other value here.
            ["Azure:BlobConnectionString"] = "UseDevelopmentStorage=true",
            ["Azure:BlobContainer"] = "insights-reports-placeholder",
            ["Budget:FreeDigestTokenCap"] = "1500",
            ["Agents:PromptDirectory"] = "./prompts",
            ["Email:TemplatePath"] = "./templates",
            ["Email:Provider"] = "elastic_email",
            ["Email:FromAddress"] = "noreply@example.com",
            ["Email:FromName"] = "RegTrack Insights",
            ["Email:UpgradeUrl"] = "https://example.com/upgrade",
            ["Email:UnsubscribeBaseUrl"] = "https://example.com/unsubscribe",
            ["Email:UnsubscribeSigningKey"] = "test-signing-key",
            ["Email:ElasticEmail:ApiKey"] = "placeholder",
            ["Llm:Provider"] = "azure_openai",
            ["Llm:AzureOpenAi:Endpoint"] = "https://example.openai.azure.com/",
            ["Llm:AzureOpenAi:Deployment"] = "gpt-4o-mini",
            ["Llm:AzureOpenAi:ApiKey"] = "placeholder",
        };

        if (overrides is not null)
            foreach (var (key, value) in overrides)
                values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInsightsData(configuration);
        services.AddInsightsFreeDigest(configuration);

        // ValidateOnBuild surfaces unresolvable dependencies here rather than at first use;
        // ValidateScopes catches a singleton capturing a scoped service, which would quietly
        // pin one tenant's scoped state for the lifetime of the process.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void EveryRegisteredServiceResolves()
    {
        using var provider = BuildProvider(BuildConfiguration());
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<IDictionaryRepository>());
        Assert.NotNull(sp.GetRequiredService<IScopeRepository>());
        Assert.NotNull(sp.GetRequiredService<IEntityRepository>());
        Assert.NotNull(sp.GetRequiredService<IEntitlementRepository>());
        Assert.NotNull(sp.GetRequiredService<IGoldenRegressionRepository>());
        Assert.NotNull(sp.GetRequiredService<IDimensionRepository>());
        Assert.NotNull(sp.GetRequiredService<IFreeDigestRepository>());
    }

    [Theory]
    [InlineData("openai", "Llm:OpenAi:ApiKey")]
    [InlineData("anthropic", "Llm:Anthropic:ApiKey")]
    public void EveryLlmProviderResolves(string provider, string apiKeyName)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = provider,
            [apiKeyName] = "placeholder",
            ["Llm:Anthropic:Model"] = "claude-sonnet-5",
        });

        using var provider2 = BuildProvider(configuration);
        Assert.NotNull(provider2.GetRequiredService<Insights.Agents.IClaudeClient>());
    }

    [Fact]
    public void FailoverEmailProviderResolves()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "failover",
            ["Email:SendGrid:ApiKey"] = "placeholder",
        });

        using var provider = BuildProvider(configuration);
        Assert.NotNull(provider.GetRequiredService<Insights.Data.Email.IEmailSender>());
    }

    /// <summary>
    /// An unrecognised provider must FAIL, and must fail while the host is still STARTING.
    /// Shipping mail - or LLM spend - through a provider nobody selected is exactly the
    /// silent-default failure the dictionary's coverage check exists to prevent elsewhere.
    /// </summary>
    [Theory]
    [InlineData("Llm:Provider")]
    [InlineData("Email:Provider")]
    public void UnknownProviderFailsAtStartup(string key)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [key] = "not-a-real-provider" });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(configuration).Dispose());
        Assert.Contains("not-a-real-provider", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Missing configuration must stop the host at startup, not at the first tenant.
    ///
    /// This is the check that caught the original registration being lazy: the values were read
    /// inside factory lambdas, so BuildServiceProvider(ValidateOnBuild: true) reported success
    /// and the failure would have surfaced on the first tenant of the first weekly run.
    /// </summary>
    [Theory]
    [InlineData("Email:FromAddress")]
    [InlineData("Email:UpgradeUrl")]
    [InlineData("Email:UnsubscribeBaseUrl")]
    [InlineData("Email:UnsubscribeSigningKey")]
    [InlineData("Agents:PromptDirectory")]
    [InlineData("Email:TemplatePath")]
    [InlineData("Llm:AzureOpenAi:ApiKey")]
    [InlineData("Email:ElasticEmail:ApiKey")]
    public void MissingRequiredConfigurationFailsAtStartup(string key)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [key] = null });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(configuration).Dispose());
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }
}

