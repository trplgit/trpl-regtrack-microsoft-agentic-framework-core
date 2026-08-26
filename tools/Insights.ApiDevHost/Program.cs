using DurableTask.Core;
using DurableTask.SqlServer;
using Insights.Api;
using Insights.Data;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Microsoft.OpenApi.Models;

/*  LOCAL HARNESS for the Insights endpoints. Not a second service - see the csproj.

    Everything below the caller is REAL: real SqlTenantDirectoryRepository against the configured
    database, real Durable Task instance store, real routing, real HTTP over a socket. The only
    stub is who is asking, because token validation lives in the RegTrack API and duplicating it
    here is exactly what CLAUDE.md 6 says produces auth bugs.                                     */

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("RegTrack")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:RegTrack is not set. Pass --ConnectionStrings:RegTrack=\"...\" or set " +
        "ConnectionStrings__RegTrack. Never point this at production.");

/*  TWO DIFFERENT DATABASES, and conflating them is a real mistake this harness made first time:
    ConnectionStrings:RegTrack is vitComplianceSystem (the tenant data), ConnectionStrings:
    DurableTaskHub is the separate task-hub database holding the dt.* schema. Pointing the
    orchestration client at RegTrack produced "Could not find stored procedure
    dt.QuerySingleOrchestration" - which reads like a missing migration, not a wrong database. */
var taskHubConnectionString = builder.Configuration.GetConnectionString("DurableTaskHub")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DurableTaskHub is not set. This is the task-hub database, NOT vitComplianceSystem.");
/*  REAL registrations, not hand-rolled ones. AddInsightsData gives the scope and tenant-directory
    repositories; AddInsightsOrchestrationClient gives TaskHubClient, IRunStatusReader and
    IInsightsRunEnqueuer; AddInsightsReportContentService gives the decrypt/serve path.

    [TRAP - this harness caused it] An earlier version registered SqlOrchestrationService and
    TaskHubClient by hand to avoid AddInsightsOrchestration, which would also have started a
    TaskHubWorker and put a second competing dequeue loop on the SHARED UAT task hub. The proper
    fix landed in WorkerRegistration: the client half is now separable, so this host gets enqueue
    and read-status WITHOUT ever dequeuing. Never call AddInsightsOrchestration here.            */
builder.Services.AddInsightsData(builder.Configuration);
builder.Services.AddInsightsOrchestrationClient(builder.Configuration);
builder.Services.AddInsightsReportContentService(builder.Configuration);

/*  DEV CALLER: reads the user id from the X-Insights-UserId header so you can switch identity in
    Swagger without restarting - which is the point, since the whole feature turns on who is
    asking. Defaults to Insights:DevUserId when the header is absent.                            */
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IInsightsCaller, HeaderInsightsCaller>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RegTrack Insights - dev harness",
        Version = "v1",
        Description =
            "LOCAL ONLY. These endpoints ship inside the RegTrack API, where authentication lives. " +
            "Here the caller is taken from the X-Insights-UserId header instead of a validated token.",
    });

    // Makes the identity switchable from the Swagger UI - Authorize, type a user id, done.
    options.AddSecurityDefinition("DevUser", new OpenApiSecurityScheme
    {
        Name = HeaderInsightsCaller.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "The User.ID to impersonate, e.g. 38. Stands in for the authenticated principal.",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "DevUser" } }] = []
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(o => o.DocumentTitle = "RegTrack Insights - dev harness");

app.MapInsightsTenantEndpoints();
app.MapInsightsRunEndpoints();

/*  The report content endpoint (decrypt + serve) and the free digest's unsubscribe/bounce pair.
    Mapped here so every Insights endpoint is exercisable from one Swagger page - the RegTrack API
    maps exactly these same four groups.                                                         */
app.MapInsightsReportContentEndpoints();
app.MapDigestEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();

/// <summary>
/// Stands in for RegTrack's authenticated principal by reading X-Insights-UserId.
///
/// [TRAP] THIS IS NOT AUTHENTICATION and must never be copied into the RegTrack API. A
/// client-supplied user id is the exact thing the IDOR guard exists to prevent - here it is
/// acceptable only because this host is local, disposable, and points at a non-production
/// database. In the real API, UserId comes from the validated token.
/// </summary>
internal sealed class HeaderInsightsCaller : IInsightsCaller
{
    public const string HeaderName = "X-Insights-UserId";

    public HeaderInsightsCaller(IHttpContextAccessor accessor, IConfiguration configuration)
    {
        var header = accessor.HttpContext?.Request.Headers[HeaderName].ToString();

        if (int.TryParse(header, out var fromHeader) && fromHeader > 0)
        {
            UserId = fromHeader;
            return;
        }

        UserId = configuration.GetValue<int>("Insights:DevUserId");

        if (UserId <= 0)
            throw new InvalidOperationException(
                $"No caller. Send {HeaderName}, or set Insights:DevUserId.");
    }

    public int UserId { get; }
}
