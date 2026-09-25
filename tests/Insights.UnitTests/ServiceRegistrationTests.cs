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
            ["Budget:FreeMonthlyTokenCap:Overview"] = "12000",
            ["Budget:FreeMonthlyTokenCap:Users"] = "9000",
            ["Budget:FreeMonthlyTokenCap:Location"] = "9000",
            ["Budget:FreeMonthlyTokenCap:Act"] = "9000",
            ["Budget:FreeMonthlyTokenCap:Licence"] = "9000",
            ["FreeDigest:Monthly:MaxDraftAttempts"] = "2",
            ["FreeDigest:Monthly:AllowPersonNames"] = "true",
            ["Agents:PromptDirectory"] = "./prompts",
            ["Email:TemplatePath"] = "./templates",
            ["Email:FromAddress"] = "noreply@example.com",
            ["Email:FromName"] = "RegTrack Insights",
            ["Email:UpgradeUrl"] = "https://example.com/upgrade",
            ["Email:UnsubscribeBaseUrl"] = "https://example.com/unsubscribe",
            ["Email:UnsubscribeSigningKey"] = "test-signing-key",
            ["Email:ElasticEmail:ApiKey"] = "placeholder",
            ["Email:SendGrid:ApiKey"] = "placeholder",
            // [MERGE FIX, 2026-09-15] Tanvi's "added base url for cdn links" commit (823865e)
            // made AddInsightsFreeDigest require this too - placeholder, never dialled, same
            // reasoning as every other value here.
            ["Email:CdnBaseUrl"] = "https://example.com/cdn",
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

    /// <summary>
    /// Per-tenant routing: BOTH providers are built at startup (any tenant can be routed to
    /// either), each behind its own rate limiter, plus the gateway resolver.
    /// </summary>
    [Fact]
    public void BothEmailProvidersAndTheGatewayResolverResolve()
    {
        using var provider = BuildProvider(BuildConfiguration());

        var registry = provider.GetRequiredService<Insights.Data.Email.IEmailSenderRegistry>();
        Assert.IsType<Insights.Data.Email.RateLimitedEmailSender>(registry.For(Insights.Data.Email.EmailGateway.ElasticEmail));
        Assert.IsType<Insights.Data.Email.RateLimitedEmailSender>(registry.For(Insights.Data.Email.EmailGateway.SendGrid));
        Assert.NotSame(registry.For(Insights.Data.Email.EmailGateway.ElasticEmail), registry.For(Insights.Data.Email.EmailGateway.SendGrid));
        Assert.NotNull(provider.GetRequiredService<Insights.Data.Email.IEmailGatewayResolver>());
    }

    /// <summary>
    /// The retired single-provider switch must stop startup, not be silently ignored - someone
    /// setting it would otherwise believe they had forced every tenant through one provider.
    /// </summary>
    [Fact]
    public void RetiredEmailProviderKeyFailsAtStartup()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { ["Email:Provider"] = "ElasticEmail" });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(configuration).Dispose());
        Assert.Contains("Email:Provider", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Email:DefaultGatewayId", "7")]
    [InlineData("Email:ElasticEmail:RateLimit:RequestsPerSecond", "0")]
    [InlineData("Email:SendGrid:RateLimit:RequestsPerSecond", "-1")]
    [InlineData("Email:HttpTimeoutSeconds", "0")]
    [InlineData("FreeDigest:Schedule:SendHourLocal", "24")]
    [InlineData("FreeDigest:Schedule:GenerateHourLocal", "-1")]
    public void InvalidEmailOrScheduleValueFailsAtStartup(string key, string value)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(configuration).Dispose());
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised provider must FAIL, and must fail while the host is still STARTING.
    /// Shipping mail - or LLM spend - through a provider nobody selected is exactly the
    /// silent-default failure the dictionary's coverage check exists to prevent elsewhere.
    /// </summary>
    [Theory]
    [InlineData("Llm:Provider")]
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
    [InlineData("Email:SendGrid:ApiKey")]
    public void MissingRequiredConfigurationFailsAtStartup(string key)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [key] = null });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(configuration).Dispose());
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// [ADDED 2026-09-16, ADR-0004] AddInsightsReportContentService was never called from any
    /// Program.cs before the combined worker/HTTP host - RunEndpoints and ReportContentEndpoints
    /// need it, and it registers three SCOPED services (IReportContentService, ICooldownRepository,
    /// IReportRequestRepository) that all depend on a scoped InsightsReportsDbContext. The risk
    /// this catches: any of those three accidentally registered (or consumed) as a singleton would
    /// be a captive dependency - one DbContext/connection held open for the process lifetime
    /// instead of one per request, exactly the trap this file's own class doc comment describes.
    /// ValidateScopes is what would catch that; ValidateOnBuild is what forces the check to run at
    /// all rather than only on first real use.
    ///
    /// Deliberately does NOT also add AddInsightsAuthentication to this same container:
    /// JwtInsightsCaller's constructor throws UnauthorizedAccessException with no HttpContext.User
    /// present, which is its whole fail-closed point (see JwtInsightsCallerTests) - not a
    /// registration defect ValidateOnBuild should be flagging. The auth registration path already
    /// has its own direct, stronger coverage in JwtAuthenticationEndToEndTests.
    /// </summary>
    [Fact]
    public void ReportContentServiceRegistrationsResolveWithoutACaptiveDependency()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Azure:TempBlobContainer"] = "insights-reports-temp",
            ["Reports:SasLifetimeMinutes"] = "10",
            ["Reports:CooldownDays"] = "30",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInsightsData(configuration);
        services.AddInsightsReportContentService(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<Insights.Data.IReportContentService>());
        Assert.NotNull(sp.GetRequiredService<Insights.Data.ICooldownRepository>());
        Assert.NotNull(sp.GetRequiredService<Insights.Data.IReportRequestRepository>());
    }
}

