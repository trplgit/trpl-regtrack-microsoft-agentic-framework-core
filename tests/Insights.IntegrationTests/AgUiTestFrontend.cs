using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.WebKey;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Trplclientsecret;

namespace Insights.IntegrationTests;

/// <summary>
/// TEST-ONLY, not production code. Two things API_CONTRACTS.md does not define yet, built here so
/// AG-UI streaming can be demoed against the REAL pipeline instead of a mock:
///
///   1. GET /api/insights/runs/{runId}/ag-ui-stream - translates our real stage-progress SSE
///      (already built, IRunStatusReader) into genuine AG-UI protocol events (RunStarted,
///      StepStarted/StepFinished per stage, RunFinished/RunError). A translation layer, not a
///      reimplementation of our own stream endpoint - that one is untouched.
///
///   2. GET /api/insights/reports/by-run/{runId}/view - decrypts and serves the finished report
///      inline. Stands in for the real read path (API_CONTRACTS.md 5: SAS mint, view-time
///      re-auth), which is not built (item 14's read half, deferred). Looks up the most recent
///      GeneratedReport row for the run's tenant - good enough for a single-run demo, NOT a real
///      reportId-keyed lookup.
///
/// Both require the same real credentials as DecryptMostRecentReportToLocalFile
/// (UatTestDataManualTests.cs) - this is that exact proven logic, wrapped as endpoints instead of
/// a one-shot test.
/// </summary>
public static class AgUiTestFrontend
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // The 6 real pipeline stages, in order - "complete" is a terminal signal, not a step.
    private static readonly string[] Stages = ["gathering", "validating", "composing", "narrating", "verifying", "rendering"];

    public static IEndpointRouteBuilder MapAgUiTestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Content(TestPageHtml, "text/html"));

        app.MapGet("/api/insights/runs/{runId}/ag-ui-stream", async (
            string runId, HttpContext http, IRunStatusReader runs, CancellationToken cancellationToken) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await WriteEventAsync(http, "RunStarted", new { type = "RunStarted", threadId = runId, runId }, cancellationToken);

            string? previousStage = null;
            // [FIX - found live 2026-08-24] A real run with reflection revisions took 15m38s to
            // Complete - past this endpoint's own 15-minute deadline, which fired a RunError right
            // as the pipeline was actually finishing successfully. 30 minutes leaves real headroom
            // above the worst case observed so far, matching the real /stream endpoint's own
            // MaxStreamDuration as a floor, not copying its exact value blindly.
            var deadline = DateTime.UtcNow.AddMinutes(30);

            while (DateTime.UtcNow < deadline)
            {
                InsightsRunStatus? current;
                try
                {
                    current = await runs.GetStatusAsync(runId, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Same tolerance as the real manual-run tests: a transient SQL blip on the
                    // client's own polling call is not the run failing - keep polling.
                    await WriteEventAsync(http, "Custom", new { type = "Custom", name = "poll_error", value = ex.Message }, cancellationToken);
                    await Task.Delay(PollInterval, cancellationToken);
                    continue;
                }

                if (current is null)
                {
                    await WriteEventAsync(http, "RunError", new { type = "RunError", message = "No such report run." }, cancellationToken);
                    return Results.Empty;
                }

                if (Array.IndexOf(Stages, current.Stage) >= 0 && current.Stage != previousStage)
                {
                    if (previousStage is not null)
                        await WriteEventAsync(http, "StepFinished", new { type = "StepFinished", stepName = previousStage }, cancellationToken);
                    await WriteEventAsync(http, "StepStarted", new { type = "StepStarted", stepName = current.Stage }, cancellationToken);
                    previousStage = current.Stage;
                }

                if (current.Status == "complete")
                {
                    if (previousStage is not null)
                        await WriteEventAsync(http, "StepFinished", new { type = "StepFinished", stepName = previousStage }, cancellationToken);
                    await WriteEventAsync(http, "RunFinished", new { type = "RunFinished", runId, outcome = new { type = "success" } }, cancellationToken);
                    return Results.Empty;
                }

                if (current.Status == "failed")
                {
                    await WriteEventAsync(http, "RunError", new { type = "RunError", message = current.Message ?? "Report generation failed." }, cancellationToken);
                    return Results.Empty;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            await WriteEventAsync(http, "RunError", new { type = "RunError", message = "Timed out waiting for the run." }, cancellationToken);
            return Results.Empty;
        });

        app.MapGet("/api/insights/reports/by-run/{runId}/view", async (
            string runId, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (!InsightsRunId.TryParse(runId, out var tenantId))
                return Results.BadRequest("Malformed runId.");

            var regTrackConnectionString = configuration.GetConnectionString("RegTrack")
                ?? throw new InvalidOperationException("ConnectionStrings:RegTrack is not configured.");

            var options = new DbContextOptionsBuilder<InsightsReportsDbContext>().UseSqlServer(regTrackConnectionString).Options;
            await using var db = new InsightsReportsDbContext(options);
            var report = await db.GeneratedReports
                .Where(r => r.CustomerId == tenantId)
                .OrderByDescending(r => r.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (report is null)
                return Results.NotFound("No persisted report found for this run's tenant yet.");

            var blobConnectionString = configuration["Azure:BlobConnectionString"]
                ?? throw new InvalidOperationException("Azure:BlobConnectionString is not configured.");
            var blobService = new BlobServiceClient(blobConnectionString);
            var blob = blobService.GetBlobContainerClient(report.BlobContainer).GetBlobClient(report.BlobPath);
            var downloaded = (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToArray();

            var iv = downloaded[..16];
            var ciphertext = downloaded[16..];

            var clientSecret = new BU().GetClientSecret();
            var kvClient = new KeyVaultClient(async (authority, resource, _) =>
            {
                var authContext = new AuthenticationContext(authority);
                var clientCred = new ClientCredential("449821d0-9ff9-4f87-a535-bda5cb484287", clientSecret);
#pragma warning disable CS0618
                var result = await authContext.AcquireTokenAsync(resource, clientCred);
#pragma warning restore CS0618
                return result.AccessToken;
            });

            var unwrapped = await kvClient.DecryptAsync(report.KeyVaultObjectVersion, JsonWebKeyEncryptionAlgorithm.RSAOAEP, report.EncryptedAesKey);

            using var aes = Aes.Create();
            aes.Key = unwrapped.Result;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            using var plaintextStream = new MemoryStream();
            await using (var cryptoStream = new CryptoStream(plaintextStream, decryptor, CryptoStreamMode.Write, leaveOpen: true))
                await cryptoStream.WriteAsync(ciphertext, cancellationToken);

            var html = Encoding.UTF8.GetString(plaintextStream.ToArray());
            return Results.Content(html, "text/html");
        });

        return app;
    }

    private static async Task WriteEventAsync(HttpContext http, string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }

    private const string TestPageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>RegInsights</title>
<style>
  body { font-family: -apple-system, "Segoe UI", sans-serif; max-width: 640px; margin: 60px auto; color: #0f172a; }
  h1 { font-size: 1.2rem; font-weight: 600; }
  p.sub { color: #64748b; font-size: 0.9rem; }
  button { font-size: 1rem; padding: 10px 22px; background: #2563eb; color: white; border: none; border-radius: 8px; cursor: pointer; }
  button:disabled { background: #cbd5e1; cursor: default; }

  .bubble { display: none; align-items: center; gap: 10px; margin: 28px 0; padding: 16px 18px;
            background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 12px; min-height: 24px; }
  .bubble.show { display: flex; }
  .bubble .text { font-size: 0.98rem; }
  .bubble .text .cursor { display: inline-block; width: 2px; height: 1em; background: #2563eb;
                           vertical-align: text-bottom; margin-left: 2px; animation: blink 1s step-start infinite; }
  @keyframes blink { 50% { opacity: 0; } }

  .dots span { display: inline-block; width: 6px; height: 6px; border-radius: 50%; background: #94a3b8;
               margin-right: 3px; animation: pulse 1.2s ease-in-out infinite; }
  .dots span:nth-child(2) { animation-delay: 0.15s; }
  .dots span:nth-child(3) { animation-delay: 0.3s; }
  @keyframes pulse { 0%, 80%, 100% { opacity: 0.25; transform: scale(0.8); } 40% { opacity: 1; transform: scale(1); } }

  .progress { display: flex; gap: 5px; margin-top: 14px; }
  .progress .dot { width: 26px; height: 4px; border-radius: 2px; background: #e2e8f0; }
  .progress .dot.done { background: #2563eb; }

  #result { margin-top: 20px; }
  #result a { color: #2563eb; font-weight: 600; text-decoration: none; }
  iframe { width: 100%; height: 600px; border: 1px solid #e2e8f0; border-radius: 10px; margin-top: 14px; }
  .error { color: #b91c1c; }

  #techToggle { color: #94a3b8; font-size: 0.75rem; cursor: pointer; margin-top: 24px; display: inline-block; }
  #tech { display: none; background: #0f172a; color: #a5f3fc; font-family: monospace; font-size: 0.75rem;
          padding: 10px; border-radius: 6px; height: 160px; overflow-y: auto; white-space: pre-wrap; margin-top: 8px; }
  #tech.show { display: block; }
</style>
</head>
<body>
<h1>RegInsights</h1>
<p class="sub">Tenant 29, compliance_health, FY2025-26 - real pipeline, real LLM cost per click.</p>
<button id="go">Generate report</button>

<div class="bubble" id="bubble">
  <span class="dots" id="dots"><span></span><span></span><span></span></span>
  <span class="text" id="bubbleText"></span>
</div>
<div class="progress" id="progress"></div>
<div id="result"></div>

<span id="techToggle">show technical log</span>
<div id="tech"></div>

<script>
const STAGES = ["gathering", "validating", "composing", "narrating", "verifying", "rendering"];
const MESSAGES = {
  gathering:  "Gathering your compliance scope...",
  validating: "Fetching and validating the nine dimensions...",
  composing:  "Deciding what matters most in this report...",
  narrating:  "Writing the narrative...",
  verifying:  "Checking every claim against the data...",
  rendering:  "Rendering the final report...",
};

const goBtn = document.getElementById("go");
const bubble = document.getElementById("bubble");
const bubbleText = document.getElementById("bubbleText");
const dots = document.getElementById("dots");
const progressEl = document.getElementById("progress");
const resultEl = document.getElementById("result");
const techEl = document.getElementById("tech");

document.getElementById("techToggle").addEventListener("click", () => {
  techEl.classList.toggle("show");
});

function techLog(line) {
  techEl.textContent += line + "\n";
  techEl.scrollTop = techEl.scrollHeight;
}

STAGES.forEach(() => {
  const d = document.createElement("div");
  d.className = "dot";
  progressEl.appendChild(d);
});

let typeToken = 0;
function typeMessage(text) {
  const myToken = ++typeToken;
  dots.style.display = "none";
  bubbleText.innerHTML = "";
  let i = 0;
  const speed = 18;
  (function step() {
    if (myToken !== typeToken) return; // superseded by a newer message
    if (i <= text.length) {
      bubbleText.innerHTML = text.slice(0, i) + '<span class="cursor"></span>';
      i++;
      setTimeout(step, speed);
    } else {
      bubbleText.innerHTML = text;
    }
  })();
}

goBtn.addEventListener("click", async () => {
  goBtn.disabled = true;
  resultEl.innerHTML = "";
  techEl.textContent = "";
  bubble.classList.add("show");
  dots.style.display = "inline-block";
  bubbleText.textContent = "";
  Array.from(progressEl.children).forEach(d => d.className = "dot");

  // A fresh period per click, not a real fiscal period - this is a demo tool, and the real
  // one-run-per-key lock (InsightsRunId) would otherwise make every click after the first
  // collide with whatever state the SAME fixed key is already in (including a stuck earlier
  // run from an interrupted worker - confirmed live 2026-08-24, OrchestrationAlreadyExistsException).
  const demoPeriod = "FY2025-26 (demo " + Date.now() + ")";

  let runId;
  try {
    techLog("POST /api/insights/reports");
    const res = await fetch("/api/insights/reports", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tenantId: 29, reportType: "compliance_health", scope: { type: "tenant" }, period: demoPeriod })
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error("HTTP " + res.status + (text ? ": " + text.slice(0, 300) : ""));
    }
    const body = await res.json();
    techLog("-> " + JSON.stringify(body));
    runId = body.runId;
  } catch (err) {
    goBtn.disabled = false;
    typeMessage("Couldn't start the run.");
    resultEl.innerHTML = '<p class="error">' + err.message + '</p>';
    techLog("ERROR: " + err.message);
    return;
  }

  const source = new EventSource("/api/insights/runs/" + runId + "/ag-ui-stream");
  source.onerror = () => techLog("EventSource error (connection issue, not necessarily a failed run)");
  let stepIndex = -1;

  ["RunStarted", "StepStarted", "StepFinished", "RunFinished", "RunError", "Custom"].forEach(eventName => {
    source.addEventListener(eventName, e => {
      const data = JSON.parse(e.data);
      techLog(eventName + ": " + e.data);

      if (eventName === "StepStarted") {
        typeMessage(MESSAGES[data.stepName] || data.stepName);
      } else if (eventName === "StepFinished") {
        stepIndex = STAGES.indexOf(data.stepName);
        if (stepIndex >= 0) progressEl.children[stepIndex].className = "dot done";
      } else if (eventName === "RunFinished") {
        source.close();
        goBtn.disabled = false;
        Array.from(progressEl.children).forEach(d => d.className = "dot done");
        typeMessage("Done - opening your report.");
        resultEl.innerHTML = '<a href="/api/insights/reports/by-run/' + runId + '/view" target="_blank">Open in new tab</a>' +
          '<iframe src="/api/insights/reports/by-run/' + runId + '/view" sandbox="allow-scripts"></iframe>';
      } else if (eventName === "RunError") {
        source.close();
        goBtn.disabled = false;
        typeMessage("Something went wrong.");
        resultEl.innerHTML = '<p class="error">' + data.message + '</p>';
      }
    });
  });
});
</script>
</body>
</html>
""";
}
