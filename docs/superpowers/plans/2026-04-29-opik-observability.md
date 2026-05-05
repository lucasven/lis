# Opik LLM Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Opik integration for LLM-specific observability — prompt/completion tracing, token usage tracking, tool call logging, and cost dashboards — while keeping Grafana for infrastructure observability.

**Architecture:** Opik accepts OpenTelemetry traces via OTLP. Since the project already has full OpenTelemetry instrumentation AND Semantic Kernel's experimental GenAI telemetry enabled, the integration is primarily configuration — add a second OTLP exporter that sends traces to Opik alongside Grafana. No code changes to the tracing itself; just add the Opik OTLP endpoint as an additional exporter. Opik will automatically parse the Semantic Kernel GenAI spans.

**Tech Stack:** OpenTelemetry OTLP exporter (already partially present via Grafana), Opik OTLP endpoint, env var configuration

---

### Task 1: Add OTLP Exporter Package

**Files:**
- Modify: `Lis.Api/Lis.Api.csproj`

- [ ] **Step 1: Check if OTLP exporter is already present**

The project uses `Grafana.OpenTelemetry` which bundles its own exporter. We need the standalone OTLP exporter to send to Opik separately.

Check if `OpenTelemetry.Exporter.OpenTelemetryProtocol` is already referenced. If not, add:

```xml
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.*" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet restore && dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Lis.Api.csproj
git commit -m "chore(api): add OpenTelemetry OTLP exporter for Opik

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Configure Opik OTLP Exporter

**Files:**
- Modify: `Lis.Api/Program.cs:36-55`

- [ ] **Step 1: Add Opik exporter alongside Grafana**

The current setup is:
```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(...)
    .WithMetrics(...)
    .WithTracing(...)
    .UseGrafana();
```

Add the Opik OTLP exporter conditionally:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(serviceName: "lis", serviceNamespace: "lis"))
    .WithMetrics(metrics => {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddRuntimeInstrumentation();
        metrics.AddMeter("Microsoft.SemanticKernel*");
    })
    .WithTracing(tracing => {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation(o => o.RecordException = true);
        tracing.AddSource("Microsoft.SemanticKernel*");
        tracing.AddSource(TraceAspect.ActivitySource.Name);

        // Opik LLM observability (sends GenAI traces to Opik via OTLP)
        if (Env("OPIK_ENABLED") == "true") {
            tracing.AddOtlpExporter("opik", options => {
                options.Endpoint = new Uri(Env("OPIK_OTLP_ENDPOINT"));
                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;

                string headers = Env("OPIK_OTLP_HEADERS");
                if (headers.Length > 0)
                    options.Headers = headers;
            });
        }
    })
    .UseGrafana();
```

Add the required using:
```csharp
using OpenTelemetry.Exporter;
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Api/Program.cs
git commit -m "feat(observability): add Opik OTLP exporter for LLM traces

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Add Opik Environment Variables

**Files:**
- Modify: `.env.example`

- [ ] **Step 1: Add Opik config entries**

```env
# Opik LLM Observability (optional)
OPIK_ENABLED=false

# For Opik Cloud:
OPIK_OTLP_ENDPOINT=https://www.comet.com/opik/api/v1/private/otel/v1/traces
OPIK_OTLP_HEADERS=Authorization=<your-api-key>,Comet-Workspace=default

# For self-hosted Opik:
# OPIK_OTLP_ENDPOINT=http://localhost:5173/api/v1/private/otel/v1/traces
# OPIK_OTLP_HEADERS=

# Optional: target a specific Opik project
# Add to headers: projectName=<your-project-name>
```

- [ ] **Step 2: Commit**

```bash
git add .env.example
git commit -m "docs: add Opik configuration to .env.example

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Verify GenAI Telemetry Switches Are Enabled

**Files:**
- Verify: `Lis.Api/Program.cs:22-24`

- [ ] **Step 1: Confirm Semantic Kernel telemetry is already enabled**

The following should already be present (and they are):
```csharp
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics",          true);
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);
```

These emit:
- `gen_ai.content.prompt` — full prompt text
- `gen_ai.content.completion` — full completion text
- `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` — token counts
- `gen_ai.request.model` — model name
- Tool call events with function names and arguments

No changes needed. This is already configured correctly.

- [ ] **Step 2: Verify by reading Program.cs lines 22-24**

Run: read the file and confirm. Already done.

---

### Task 5: Enrich Traces with Opik-Relevant Tags

**Files:**
- Modify: `Lis.Agent/ConversationService.cs` (the RespondAsync method)

- [ ] **Step 1: Read RespondAsync to see what tags are already set**

Read the relevant section of ConversationService.

- [ ] **Step 2: Add LLM-relevant tags for Opik**

Opik parses standard OpenTelemetry GenAI semantic conventions. Add tags that enrich the trace:

```csharp
Activity.Current?.SetTag("gen_ai.system", "anthropic");
Activity.Current?.SetTag("session.id", session.Id.ToString());
Activity.Current?.SetTag("agent.name", agent.DisplayName ?? agent.Name);
```

Only add these if they're not already being set. Don't duplicate existing tags.

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 4: Commit**

```bash
git add Lis.Agent/ConversationService.cs
git commit -m "feat(observability): enrich traces with GenAI semantic convention tags

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: End-to-End Verification

- [ ] **Step 1: Build and test**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: SUCCESS

- [ ] **Step 2: Test with self-hosted Opik (optional)**

If you have a local Opik instance:
```bash
OPIK_ENABLED=true OPIK_OTLP_ENDPOINT=http://localhost:5173/api/v1/private/otel/v1/traces cd Lis.Api && dotnet run
```

Send a test message and verify traces appear in the Opik dashboard.

- [ ] **Step 3: Test with Opik Cloud (optional)**

Set `OPIK_OTLP_ENDPOINT` and `OPIK_OTLP_HEADERS` with your Comet API key.

- [ ] **Step 4: Verify Grafana still works**

Confirm that the Grafana exporter (`.UseGrafana()`) continues to receive traces normally — the OTLP exporter should be additive, not replace Grafana.

- [ ] **Step 5: Code cleanup**

Run: `jb cleanupcode Lis.Api/Lis.Api.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings`

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "feat(observability): complete Opik LLM observability integration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```
