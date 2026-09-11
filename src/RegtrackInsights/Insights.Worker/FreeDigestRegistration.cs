using Insights.Agents;
using Insights.Data;
using Insights.Data.Email;
using Insights.Persistence;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker;

/// <summary>
/// Registers the free weekly digest: LLM client, prompt loader, writer, renderer, email sender,
/// pipeline and <see cref="IFreeDigestService"/>.
///
/// Call AFTER AddInsightsData - the pipeline depends on IFreeDigestRepository, IScopeRepository
/// and IDictionaryRepository from there.
///
/// Every value comes from configuration. Nothing here is a literal, which is the point: the code
/// path a manual test exercises is the one the scheduler and the RegTrack API use, so a passing
/// test says something about production.
/// </summary>
public static class FreeDigestRegistration
{
    private const string LlmClientName = "insights-llm";
    private const string EmailClientName = "insights-email";

    public static IServiceCollection AddInsightsFreeDigest(this IServiceCollection services, IConfiguration configuration)
    {
        /*  [TRAP] A FACTORY LAMBDA IS NOT STARTUP VALIDATION.
            Registering these as `_ => Require(configuration, ...)` compiles, and
            BuildServiceProvider(ValidateOnBuild: true) still reports success - the validator
            checks that the dependency GRAPH can be satisfied, and cannot see inside a lambda it
            has not run. Missing configuration would then surface on the first tenant of the
            first weekly run, in production, at 3am.

            So every required value is read HERE, eagerly, and captured. A missing key throws
            while the host is still starting, which is the whole reason for reading it early.   */
        var settings = BuildSettings(configuration);
        var promptDirectory = Require(configuration, "Agents:PromptDirectory");
        var templateDirectory = Require(configuration, "Email:TemplatePath");
        var chatClientFactory = BuildChatClientFactory(configuration);
        var emailSenderFactory = BuildEmailSenderFactory(configuration);

        /*  Named HttpClients via the factory, never `new HttpClient()`. A long-lived host that
            constructs its own clients exhausts sockets; one holding a single static client never
            picks up DNS changes. The factory solves both.                                      */
        services.AddHttpClient(LlmClientName);
        services.AddHttpClient(EmailClientName);

        services.AddSingleton(settings);
        services.AddSingleton<FreeDigestMetrics>();
        services.AddSingleton<IPromptLoader>(_ => new FilePromptLoader(promptDirectory));
        services.AddSingleton(_ => new FreeDigestEmailRenderer(templateDirectory));

        services.AddSingleton(sp =>
            chatClientFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmClientName)));

        /*  ADR-0001 (2026-09-10) - every send in this worker passes through the rate limiter here,
            wrapping whichever concrete provider (or FailoverEmailSender pair) the factory above
            selected. This is the ONLY IEmailSender registration - nothing sends unrated-limited.  */
        services.AddSingleton<IEmailSender>(sp =>
        {
            var inner = emailSenderFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(EmailClientName));
            return new RateLimitedEmailSender(inner, settings.EmailRateLimitPerSecond, settings.EmailRateLimitAcquireTimeout);
        });

        services.AddSingleton<FreeDigestWriter>();

        /*  ADR-0001 (2026-09-10) - the artifact index (sql/29) and its blob store. Reuses the SAME
            encryption scheme the paid pipeline uses (RegisterReportCodec, TryAddSingleton - safe
            to call again even if the paid write path already registered it, see that method's own
            doc comment), but writes to a SEPARATE container so nothing here can collide with, slow
            down, or be confused with a paid report. A dedicated AzureReportBlobWriter instance is
            constructed INLINE rather than through DI's own AzureReportBlobWriter registration,
            because TryAddSingleton keys on TYPE - a second `TryAddSingleton<AzureReportBlobWriter>`
            here would silently be skipped and this would end up writing digests into the PAID
            container instead of its own.                                                        */
        WorkerRegistration.RegisterReportCodec(services, configuration);

        var digestBlobConnectionString = Require(configuration, "Azure:BlobConnectionString");
        var digestBlobContainer = configuration["Azure:DigestBlobContainer"] ?? "insights-digests";
        services.AddSingleton<IDigestArtifactStore>(sp => new AzureDigestArtifactStore(
            sp.GetRequiredService<IReportEncryptor>(),
            sp.GetRequiredService<IReportDecryptor>(),
            new AzureReportBlobWriter(digestBlobConnectionString, digestBlobContainer)));

        var regTrackConnectionString = Require(configuration, "ConnectionStrings:RegTrack");
        services.AddSingleton<IFreeDigestArtifactRepository>(_ => new SqlFreeDigestArtifactRepository(regTrackConnectionString));

        /*  The weekly lane (ADR-0001). Registered unconditionally but INERT unless
            FreeDigest:Schedule:Enabled is true - a worker started for any other reason must not
            begin mailing customers because it happened to boot.                                */
        services.AddHostedService<FreeDigestScheduler>();

        return services;
    }

    private static FreeDigestSettings BuildSettings(IConfiguration configuration) => new()
    {
        TokenCap = configuration.GetValue<int?>("Budget:FreeDigestTokenCap")
                   ?? throw new InvalidOperationException("Budget:FreeDigestTokenCap is not configured."),
        FromAddress = Require(configuration, "Email:FromAddress"),
        FromName = configuration["Email:FromName"] ?? "RegTrack Insights",
        UpgradeUrl = Require(configuration, "Email:UpgradeUrl"),
        UnsubscribeBaseUrl = Require(configuration, "Email:UnsubscribeBaseUrl"),
        UnsubscribeSigningKey = Require(configuration, "Email:UnsubscribeSigningKey"),
        RecipientOverride = configuration["Email:RecipientOverride"],
        ScheduleEnabled = configuration.GetValue("FreeDigest:Schedule:Enabled", false),
        ScheduleCheckInterval = TimeSpan.FromMinutes(configuration.GetValue("FreeDigest:Schedule:CheckIntervalMinutes", 60)),
        SchedulePerTenantDelay = TimeSpan.FromMilliseconds(configuration.GetValue("FreeDigest:Schedule:PerTenantDelayMs", 250)),

        // ADR-0001 (2026-09-10) - the two-phase schedule. Default IST: confirmed by the product
        // owner as the intended zone for "9am" (see FreeDigestScheduler's own doc comment).
        ScheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration["FreeDigest:Schedule:TimeZone"] ?? "India Standard Time"),
        GenerateDay = Enum.Parse<DayOfWeek>(configuration["FreeDigest:Schedule:GenerateDay"] ?? "Sunday"),
        SendDay = Enum.Parse<DayOfWeek>(configuration["FreeDigest:Schedule:SendDay"] ?? "Monday"),
        SendHourLocal = configuration.GetValue("FreeDigest:Schedule:SendHourLocal", 9),
        ArtifactFreshnessDays = configuration.GetValue("FreeDigest:Artifact:FreshnessDays", 3),
        ArtifactRetentionDays = configuration.GetValue("FreeDigest:Artifact:RetentionDays", 90),
        EmailRateLimitPerSecond = configuration.GetValue("Email:RateLimit:RequestsPerSecond", 5),
        EmailRateLimitAcquireTimeout = TimeSpan.FromSeconds(configuration.GetValue("Email:RateLimit:AcquireTimeoutSeconds", 30)),
    };

    /*  Both factories validate their provider name AND its keys eagerly, then return a closure
        that only needs the HttpClient. Adding a provider is one case arm plus a config value.

        Both FAIL CLOSED on an unrecognised name. Falling back to a default would mean shipping
        mail, or spending LLM budget, through something nobody selected - the same silent-default
        failure the dictionary's coverage check exists to prevent elsewhere.                    */
    private static Func<HttpClient, IClaudeClient> BuildChatClientFactory(IConfiguration configuration)
    {
        var provider = Require(configuration, "Llm:Provider");

        switch (Normalise(provider))
        {
            case "azureopenai":
                var endpoint = Require(configuration, "Llm:AzureOpenAi:Endpoint");
                var deployment = Require(configuration, "Llm:AzureOpenAi:Deployment");
                var azureKey = Require(configuration, "Llm:AzureOpenAi:ApiKey");
                return http => new AzureOpenAiChatClient(http, endpoint, deployment, azureKey);

            case "openai":
                var openAiKey = Require(configuration, "Llm:OpenAi:ApiKey");
                var openAiModel = configuration["Llm:OpenAi:Model"] ?? "gpt-4o-mini";
                return http => new OpenAiChatClient(http, openAiKey, openAiModel);

            case "anthropic":
                var anthropicKey = Require(configuration, "Llm:Anthropic:ApiKey");
                var anthropicModel = Require(configuration, "Llm:Anthropic:Model");
                return http => new AnthropicClaudeClient(http, anthropicKey, anthropicModel);

            default:
                throw new InvalidOperationException(
                    $"Unknown Llm:Provider '{provider}'. Expected azure_openai, openai or anthropic.");
        }
    }

    private static Func<HttpClient, IEmailSender> BuildEmailSenderFactory(IConfiguration configuration)
    {
        var provider = Require(configuration, "Email:Provider");

        switch (Normalise(provider))
        {
            case "elasticemail":
                var elasticKey = Require(configuration, "Email:ElasticEmail:ApiKey");
                return http => new ElasticEmailSender(http, elasticKey);

            case "sendgrid":
                var sendGridKey = Require(configuration, "Email:SendGrid:ApiKey");
                return http => new SendGridEmailSender(http, sendGridKey);

            case "failover":
                /*  Order matters: FailoverEmailSender only tries the secondary when the primary
                    throws, so the pair is not symmetric.                                        */
                var primaryKey = Require(configuration, "Email:ElasticEmail:ApiKey");
                var secondaryKey = Require(configuration, "Email:SendGrid:ApiKey");
                return http => new FailoverEmailSender(
                    new ElasticEmailSender(http, primaryKey),
                    new SendGridEmailSender(http, secondaryKey));

            default:
                throw new InvalidOperationException(
                    $"Unknown Email:Provider '{provider}'. Expected elastic_email, sendgrid or failover.");
        }
    }

    /// <summary>
    /// Provider names are compared case- and separator-insensitively, so "ElasticEmail",
    /// "elastic_email" and "elastic-email" all select the same implementation.
    ///
    /// This is NOT a loosening of the fail-closed rule - an unrecognised name still throws. It
    /// only stops a config file written in one casing convention from refusing to start against
    /// code written in another, a failure that says nothing about correctness.
    /// </summary>
    private static string Normalise(string provider) =>
        provider.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>
    /// Fails at STARTUP on a missing value, not at the first send - see the trap note above for
    /// why that distinction needed work to be true. Same stance AddInsightsData takes on the
    /// connection string.
    /// </summary>
    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}





