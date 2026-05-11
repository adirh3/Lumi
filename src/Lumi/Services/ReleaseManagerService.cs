using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public sealed class ReleaseManagerService
{
    public const string AgentName = "Release Manager";

    private static readonly string[] CapabilityDirectories =
    [
        @".github\agents",
        @".github\skills",
        @".github\prompts",
        @".rema\agents",
        @".rema\skills",
        @".lumi\agents",
        @".lumi\skills",
        @".copilot\agents",
        @".copilot\skills",
        @".ai\agents",
        @".ai\skills"
    ];

    private static readonly string[] CapabilityFiles =
    [
        "AGENTS.md",
        "capabilities.json",
        "rema-capabilities.json",
        @".vscode\mcp.json",
        ".mcp.json",
        "mcp.json",
        @".rema\mcp.json",
        @".lumi\mcp.json",
        @".copilot\mcp.json",
        @".github\mcp.json"
    ];

    private readonly DataStore _dataStore;
    private readonly Action? _releaseDataChanged;

    public ReleaseManagerService(DataStore dataStore, Action? releaseDataChanged = null)
    {
        _dataStore = dataStore;
        _releaseDataChanged = releaseDataChanged;
    }

    public static bool IsReleaseManagerAgent(LumiAgent? agent)
        => string.Equals(agent?.Name, AgentName, StringComparison.OrdinalIgnoreCase);

    internal static bool EnsureReleaseManagerLumi(AppData data)
    {
        var desired = CreateReleaseManagerAgent();
        var existing = data.Agents.FirstOrDefault(agent =>
            agent.Name.Equals(desired.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            data.Agents.Add(desired);
            return true;
        }

        if (!existing.IsBuiltIn)
            return false;

        var changed = false;
        if (existing.Description != desired.Description)
        {
            existing.Description = desired.Description;
            changed = true;
        }

        if (existing.SystemPrompt != desired.SystemPrompt)
        {
            existing.SystemPrompt = desired.SystemPrompt;
            changed = true;
        }

        if (existing.IconGlyph != desired.IconGlyph)
        {
            existing.IconGlyph = desired.IconGlyph;
            changed = true;
        }

        if (!existing.IsBuiltIn)
        {
            existing.IsBuiltIn = true;
            changed = true;
        }

        return changed;
    }

    internal static LumiAgent CreateReleaseManagerAgent()
    {
        return new LumiAgent
        {
            Name = AgentName,
            Description = "Plans, validates, drafts, and monitors R2D/SafeFly releases with explicit approval gates",
            IconGlyph = "R",
            IsBuiltIn = true,
            SystemPrompt = """
                You are the Release Manager Lumi.

                Your job is to reduce release-request toil while keeping safety decisions explicit. Stay chat-native: use normal Lumi chats, MCPs, projects, skills, and background jobs. Do not create a Rema dashboard, shift view, hotkey flow, updater, or standalone release shell.

                ## Capability preference order
                1. Repo-provided release agents or subagents.
                2. Repo release, health, and change-validation skills or prompts.
                3. Repo MCPs/tools and configured Lumi MCP servers.
                4. Built-in Release Manager helper tools.
                5. Generic release reasoning.

                Always review capability output. Reject or qualify answers that use the wrong service, version, stage, ring, region, stamp, or time window; omit change validation; make vague health claims; contradict stronger evidence; or cannot prove the scope.

                ## Required proof chain
                Build a definitive proof chain before recommendations:
                source commit/PR -> build pipeline -> artifact/version -> deployment pipeline/stage -> runtime version confirmation -> health telemetry -> change validation -> recommendation.

                Required proof includes exact commit/branch, PRs/work items, changed files, build/run ID, artifact identity, exact version, exact stage/ring/environment/region/stamp, deployment time, runtime version evidence, scoped health telemetry, changed-behavior validation, dependencies, previous good version, and rollback path.

                If proof cannot isolate version, scope, time window, runtime state, health, or changed behavior, say "not definitively validated" and list the missing proof. Broad service health is context only. Service-up is not proof that changed behavior works.

                ## Playbooks
                - Candidate selection: rank candidates, explain why the selected build is safest, list alternatives, and state why each was rejected or deferred. Do not pick the latest build unless evidence supports it.
                - Pipeline triage: order next actions by customer impact, rollback/pause decisions, user approvals, failed change validation, dependency blockers, R2D/SafeFly bottlenecks, stale runs, monitoring-only items, and information.
                - Smallest safe scope: validate only affected components and direct dependents. Prefer one INT stamp or one canary/stage first. Exclude unrelated services, components, stamps, and broad regions.
                - R2D/SafeFly V2: discover the best AutoCreate path, create local drafts from evidence, validate required fields, prepare approval-gated external handoff, track request state, and recommend lease/async/live/draft-only/certified Geneva Action paths.
                - Lease reasoning: lease signals can support low-risk repeatable changes, but never replace proof-chain validation. Reject lease use when blast radius, validation, monitoring, manual steps, or sensitive paths are unclear.
                - Async review and Teams Pinger: draft reviewer summaries and communications from evidence, but do not send anything without approval.
                - Project onboarding: when onboarding a new project, repo, or service, attempt Work IQ setup plus MCP installation and activation. Check workspace MCP config and configured Lumi MCP servers; if setup is missing or blocked, state exactly what remains unavailable.
                - Validation issue owner outreach: when code validation fails, is inconclusive, or exposes an issue in changed code, identify the code owner for the changed files/components and suggest reaching out via Teams/email. Draft outreach only; do not send anything without approval.
                - Background monitoring: create Lumi background jobs for useful wakeups such as waiting-for-info, approval, stale review, stage completion/failure, health degradation, change-validation readiness/failure, or user action needed when the user asks to monitor.
                - Resume flow: list recent evidence packets or SafeFly drafts before recreating release state in follow-up turns.

                ## Approval gates
                Never do these without explicit user approval for the exact action, scope, version, and risk: create or submit production R2D/SafeFly, send Teams/email, promote stages, approve stages, restart, rollback, trigger risky deployments, or deploy broad scope.

                ## Output expectations
                Prefer concise structured sections with:
                status, service, goal, selected_candidate or active_release_summary, proof_chain, missing_proof, risk, review_path, recommended_next_action, requires_user_approval, and evidence links.

                When no real AutoCreate interface is available, do not pretend a request was submitted. Produce a validated draft and list the missing integration.
                """
        };
    }

    public List<AIFunction> BuildTools(Guid chatId, string workDir)
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("Repository root to inspect. Omit to use the active chat working directory.")] string? repoPath = null) =>
                    DiscoverCapabilities(string.IsNullOrWhiteSpace(repoPath) ? workDir : repoPath),
                "release_discover_capabilities",
                "Discover release-manager repo capabilities, including agents, skills, prompts, capability JSON, and MCP config. Malformed JSON is reported as a warning instead of being ignored."),

            AIFunctionFactory.Create(
                ([Description("Repository root to inspect. Omit to use the active chat working directory.")] string? repoPath = null) =>
                    DiscoverR2dCapabilities(string.IsNullOrWhiteSpace(repoPath) ? workDir : repoPath),
                "r2d_discover_capabilities",
                "Discover available R2D/SafeFly AutoCreate capabilities from repo files, workspace MCP config, and configured Lumi MCP servers."),

            AIFunctionFactory.Create(
                ([Description("Repository root to inspect. Omit to use the active chat working directory.")] string? repoPath = null) =>
                    DiscoverEvidenceAdapters(string.IsNullOrWhiteSpace(repoPath) ? workDir : repoPath),
                "release_discover_evidence_adapters",
                "Discover ADO, EV2, Kusto/ADX, release-analysis, runtime-version, health, and change-validation evidence adapters."),

            AIFunctionFactory.Create(
                ([Description("Service or component name.")] string service,
                 [Description("Release goal, such as candidate selection, R2D draft, validation, or monitoring.")] string goal,
                 [Description("Exact target scope: stage, ring, environment, region, stamp, and deployment metadata.")] string? targetScope = null,
                 [Description("Candidate version or build number.")] string? candidateVersion = null,
                 [Description("Source proof: commit, branch, PRs, work items, and changed files.")] string? sourceChange = null,
                 [Description("Build proof: pipeline name/ID, run ID, branch, tests, and artifact.")] string? build = null,
                 [Description("Artifact proof: exact version/build and artifact identity.")] string? artifactVersion = null,
                 [Description("Deployment proof: pipeline, stage/ring/region/stamp, and deployment time.")] string? deployment = null,
                 [Description("Runtime proof that the target scope is running the candidate version.")] string? runtimeVersion = null,
                 [Description("Scoped health telemetry with version/stage/time-window filters.")] string? healthTelemetry = null,
                 [Description("Proof that the newly changed behavior or code path works.")] string? changeValidation = null,
                 [Description("Previous good version and rollback path.")] string? rollbackPlan = null,
                 [Description("Risk summary or risk-signal output.")] string? riskSummary = null,
                 [Description("Recommended next action.")] string? recommendedNextAction = null,
                 [Description("Whether the recommended next action requires user approval.")] bool requiresUserApproval = true) =>
                    CreateEvidencePacket(chatId, service, goal, targetScope, candidateVersion, sourceChange, build,
                        artifactVersion, deployment, runtimeVersion, healthTelemetry, changeValidation, rollbackPlan,
                        riskSummary, recommendedNextAction, requiresUserApproval),
                "release_build_evidence_packet",
                "Persist a release evidence packet and review the proof chain. Missing links are marked not definitively validated."),

            AIFunctionFactory.Create(
                ([Description("Release evidence packet ID.")] string evidencePacketId) =>
                    ReviewEvidencePacket(evidencePacketId),
                "release_review_evidence_packet",
                "Review a persisted release evidence packet for missing proof, weak claims, and approval-gated next actions."),

            AIFunctionFactory.Create(
                ([Description("Maximum recent evidence packets to list.")] int maxItems = 5,
                 [Description("Include evidence packets from all chats instead of only this chat.")] bool includeAllChats = false) =>
                    ListEvidencePackets(chatId, maxItems, includeAllChats),
                "release_list_evidence_packets",
                "List recent persisted release evidence packets so follow-up release chats can resume prior work."),

            AIFunctionFactory.Create(
                ([Description("Service or component name.")] string service,
                 [Description("Exact target scope: stage, ring, environment, region, stamp, and deployment metadata.")] string targetScope,
                 [Description("Candidate version or build number.")] string? candidateVersion = null,
                 [Description("Source change summary: commit, branch, PRs, work items, and changed files.")] string? sourceChange = null,
                 [Description("Changed components or files, separated by new lines, commas, or semicolons.")] string? changedComponents = null,
                 [Description("Direct dependencies, separated by new lines, commas, or semicolons.")] string? directDependencies = null,
                 [Description("Known health query/dashboard hints.")] string? healthHints = null) =>
                    DraftEvidenceCollectionPlan(service, targetScope, candidateVersion, sourceChange, changedComponents, directDependencies, healthHints),
                "release_draft_evidence_collection_plan",
                "Draft the exact source/build/deploy/runtime/telemetry/change-validation evidence collection plan for available repo/MCP adapters."),

            AIFunctionFactory.Create(
                ([Description("Optional release evidence packet ID to map into the draft.")] string? evidencePacketId = null,
                 [Description("Service or component name.")] string? service = null,
                 [Description("Candidate version or build.")] string? candidateVersion = null,
                 [Description("Exact target scope for R2D/SafeFly.")] string? targetScope = null,
                 [Description("Change type, such as App Deployment, Data Deployment, Production Touch, config-only, feature flag, or certified Geneva Action.")] string? changeType = null,
                 [Description("Discovered AutoCreate capability or 'draft-only fallback'.")] string? autoCreateCapability = null,
                 [Description("Risk summary.")] string? riskSummary = null,
                 [Description("Validation and changed-behavior proof summary.")] string? validationSummary = null,
                 [Description("Health telemetry summary.")] string? healthSummary = null,
                 [Description("Bake time summary.")] string? bakeTimeSummary = null,
                 [Description("Rollback plan.")] string? rollbackPlan = null,
                 [Description("Communications plan or reviewer summary draft.")] string? communicationsPlan = null,
                 [Description("Review path recommendation: lease-backed, async review, live review, draft only, or certified Geneva Action.")] string? reviewPathRecommendation = null,
                 [Description("Links separated by new lines, commas, or semicolons.")] string? links = null) =>
                    CreateSafeFlyDraft(chatId, evidencePacketId, service, candidateVersion, targetScope, changeType,
                        autoCreateCapability, riskSummary, validationSummary, healthSummary, bakeTimeSummary,
                        rollbackPlan, communicationsPlan, reviewPathRecommendation, links),
                "r2d_create_draft",
                "Create a local SafeFly V2/R2D draft from release evidence. This never submits the request."),

            AIFunctionFactory.Create(
                ([Description("SafeFly/R2D draft ID.")] string draftId) =>
                    ValidateSafeFlyDraft(draftId),
                "r2d_validate_draft",
                "Validate a local SafeFly V2/R2D draft for required metadata and missing proof. Submission remains approval-gated."),

            AIFunctionFactory.Create(
                ([Description("SafeFly/R2D draft ID.")] string draftId) =>
                    GetSafeFlyDraft(draftId),
                "r2d_get_draft",
                "Fetch a local SafeFly V2/R2D draft without submitting or modifying it."),

            AIFunctionFactory.Create(
                ([Description("Maximum recent SafeFly/R2D drafts to list.")] int maxItems = 5,
                 [Description("Include drafts from all chats instead of only this chat.")] bool includeAllChats = false) =>
                    ListSafeFlyDrafts(chatId, maxItems, includeAllChats),
                "r2d_list_drafts",
                "List recent local SafeFly V2/R2D drafts so follow-up release chats can resume prior work."),

            AIFunctionFactory.Create(
                ([Description("SafeFly/R2D draft ID.")] string draftId,
                 [Description("Exact approved action, such as 'create SafeFly request from this draft'.")] string approvedAction,
                 [Description("Exact approved target scope. Must match the draft scope.")] string approvedScope,
                 [Description("Exact approved candidate version. Must match the draft version.")] string approvedVersion,
                 [Description("Exact approved risk acknowledgement.")] string approvedRisk,
                 [Description("External AutoCreate capability or MCP/tool the user approved using.")] string? externalCapability = null) =>
                    PrepareApprovedR2dRequest(draftId, approvedAction, approvedScope, approvedVersion, approvedRisk, externalCapability),
                "r2d_prepare_approved_request",
                "Prepare an approval-gated external R2D/SafeFly handoff. This validates exact action/scope/version/risk approval and never submits by itself."),

            AIFunctionFactory.Create(
                ([Description("SafeFly/R2D draft ID.")] string draftId,
                 [Description("External R2D/SafeFly request ID, if known.")] string? requestId = null,
                 [Description("Current external request status, such as waiting-for-info, approved, blocked, submitted, or completed.")] string? requestStatus = null,
                 [Description("External request status URL, if known.")] string? statusUrl = null,
                 [Description("Short status evidence summary.")] string? summary = null) =>
                    UpdateR2dRequestStatus(draftId, requestId, requestStatus, statusUrl, summary),
                "r2d_update_request_status",
                "Update local R2D/SafeFly request status evidence after checking the external system."),

            AIFunctionFactory.Create(
                ([Description("SafeFly/R2D draft ID.")] string draftId) =>
                    GetR2dRequestStatus(draftId),
                "r2d_get_request_status",
                "Read local R2D/SafeFly request status evidence without modifying it."),

            AIFunctionFactory.Create(
                ([Description("Release evidence packet ID, if available.")] string? evidencePacketId = null,
                  [Description("Service or component name.")] string? service = null,
                  [Description("Current release or SafeFly status URL.")] string? statusUrl = null,
                  [Description("Wake conditions, separated by new lines, commas, or semicolons.")] string? wakeConditions = null) =>
                    DraftMonitorPrompt(evidencePacketId, service, statusUrl, wakeConditions),
                "release_draft_monitor_prompt",
                "Draft a Lumi background-job prompt for release monitoring. It does not create the job by itself."),

            AIFunctionFactory.Create(
                ([Description("Release evidence packet ID, if available.")] string? evidencePacketId = null,
                 [Description("SafeFly/R2D draft ID, if available.")] string? draftId = null,
                 [Description("Service or component name.")] string? service = null,
                 [Description("Current release, pipeline, dashboard, or SafeFly status URL.")] string? statusUrl = null,
                 [Description("Wake conditions, separated by new lines, commas, or semicolons.")] string? wakeConditions = null,
                 [Description("Polling interval in minutes for a recurring time job.")] int intervalMinutes = 15,
                 [Description("Queue the monitor job immediately after creation.")] bool runNow = false) =>
                    CreateMonitorJob(chatId, evidencePacketId, draftId, service, statusUrl, wakeConditions, intervalMinutes, runNow),
                "release_create_monitor_job",
                "Create a Lumi background job that wakes this chat only when release monitoring needs attention."),

            AIFunctionFactory.Create(
                ([Description("Repository root to inspect. Omit to use the active chat working directory.")] string? repoPath = null) =>
                    DiscoverWorkIqCapabilities(string.IsNullOrWhiteSpace(repoPath) ? workDir : repoPath),
                "release_discover_workiq_capabilities",
                "Discover Work IQ context/communications MCP capabilities from repo and Lumi MCP configuration."),

            AIFunctionFactory.Create(
                ([Description("Release evidence packet ID, if available.")] string? evidencePacketId = null,
                 [Description("SafeFly/R2D draft ID, if available.")] string? draftId = null,
                 [Description("Message kind, such as release start, stage completed, failure, rollback recommended, or waiting for info.")] string? messageKind = null,
                 [Description("Audience or channel context.")] string? audience = null,
                 [Description("Additional context to include.")] string? additionalContext = null) =>
                    DraftReleaseCommunications(evidencePacketId, draftId, messageKind, audience, additionalContext),
                "release_draft_comms",
                "Draft release communications from evidence. It never sends Teams/email and always keeps send approval explicit."),

            AIFunctionFactory.Create(
                ([Description("Path to Rema data.json. Omit to use %AppData%\\Rema\\data.json.")] string? dataJsonPath = null,
                 [Description("Preview import without writing Lumi release profiles.")] bool previewOnly = true) =>
                    ImportRemaData(dataJsonPath, previewOnly),
                "release_import_rema_data",
                "Preview or import Rema service metadata into Lumi release profiles without importing dashboard or shift UX."),

            AIFunctionFactory.Create(
                ([Description("Service or component name.")] string service,
                 [Description("Candidate state.")] string? candidateState = null,
                 [Description("Pipeline state.")] string? pipelineState = null,
                 [Description("Deployment state.")] string? deploymentState = null,
                 [Description("Health state.")] string? healthState = null,
                 [Description("Change-validation state.")] string? changeValidationState = null,
                 [Description("R2D/SafeFly state.")] string? r2dState = null,
                 [Description("Approval state.")] string? approvalState = null) =>
                    DraftNextActionQueue(service, candidateState, pipelineState, deploymentState, healthState,
                        changeValidationState, r2dState, approvalState),
                "release_draft_next_action_queue",
                "Produce a small ordered next-action queue across release state dimensions.")
        ];
    }

    private string DiscoverCapabilities(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return $"No release capabilities discovered. Repository path does not exist: {repoPath}";

        var repoAgents = new List<string>();
        var repoSkillsOrPrompts = new List<string>();
        var repoMcpConfigs = new List<string>();
        var capabilityManifests = new List<string>();
        var generalInstructions = new List<string>();
        var warnings = new List<string>();

        foreach (var directory in CapabilityDirectories)
        {
            var fullPath = Path.Combine(repoPath, directory);
            if (!Directory.Exists(fullPath))
                continue;

            var files = Directory.GetFiles(fullPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
                continue;

            var entry = $"{directory}: {string.Join(", ", files)}";
            if (directory.Contains("agents", StringComparison.OrdinalIgnoreCase))
                repoAgents.Add(entry);
            else
                repoSkillsOrPrompts.Add(entry);
        }

        foreach (var file in CapabilityFiles)
        {
            var fullPath = Path.Combine(repoPath, file);
            if (!File.Exists(fullPath))
                continue;

            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ValidateJsonFile(fullPath, warnings);
                if (file.Contains("mcp", StringComparison.OrdinalIgnoreCase))
                    repoMcpConfigs.Add(FormatMcpConfigSummary(fullPath, file));
                else
                    capabilityManifests.Add(file);
            }
            else
            {
                generalInstructions.Add(file);
            }
        }

        var foundCount = repoAgents.Count + repoSkillsOrPrompts.Count + repoMcpConfigs.Count + capabilityManifests.Count + generalInstructions.Count;
        var builder = new StringBuilder();
        builder.AppendLine(foundCount == 0
            ? "status: no repo release capabilities found"
            : "status: discovered repo release capabilities");
        builder.AppendLine($"repo_path: {repoPath}");
        AppendCapabilitySection(builder, "repo_agents_or_subagents", repoAgents);
        AppendCapabilitySection(builder, "repo_release_skills_or_prompts", repoSkillsOrPrompts);
        AppendCapabilitySection(builder, "repo_mcp_configs", repoMcpConfigs);
        AppendCapabilitySection(builder, "capability_manifests", capabilityManifests);
        AppendCapabilitySection(builder, "general_instructions", generalInstructions);
        builder.AppendLine("preference_order: repo agents/subagents -> repo release/health/change-validation skills -> repo MCPs/tools -> built-in Release Manager tools -> generic reasoning");
        builder.AppendLine("recommended_next_tool_path: review the highest-ranked repo capability first, then use release_build_evidence_packet to capture the proof chain before drafting R2D/SafeFly.");

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Capability warnings:");
            foreach (var warning in warnings)
                builder.AppendLine("- " + warning);
        }

        return builder.ToString().Trim();
    }

    private static void AppendCapabilitySection(StringBuilder builder, string title, List<string> items)
    {
        builder.AppendLine($"{title}:");
        if (items.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var item in items.Order(StringComparer.OrdinalIgnoreCase))
            builder.AppendLine("- " + item);
    }

    private static string FormatMcpConfigSummary(string fullPath, string displayPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var servers = ReadMcpServerNames(document.RootElement);
            return servers.Count == 0
                ? displayPath
                : $"{displayPath}: {string.Join(", ", servers)}";
        }
        catch (JsonException)
        {
            return displayPath;
        }
        catch (IOException)
        {
            return displayPath;
        }
        catch (UnauthorizedAccessException)
        {
            return displayPath;
        }
    }

    private static List<string> ReadMcpServerNames(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        if (!root.TryGetProperty("servers", out var servers) &&
            !root.TryGetProperty("mcpServers", out servers))
            return [];

        if (servers.ValueKind != JsonValueKind.Object)
            return [];

        return servers.EnumerateObject()
            .Select(server => server.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateJsonFile(string path, List<string> warnings)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            _ = document.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            warnings.Add($"{path}: malformed JSON ({ex.Message})");
        }
        catch (IOException ex)
        {
            warnings.Add($"{path}: could not read ({ex.Message})");
        }
        catch (UnauthorizedAccessException ex)
        {
            warnings.Add($"{path}: access denied ({ex.Message})");
        }
    }

    private string CreateEvidencePacket(
        Guid chatId,
        string service,
        string goal,
        string? targetScope,
        string? candidateVersion,
        string? sourceChange,
        string? build,
        string? artifactVersion,
        string? deployment,
        string? runtimeVersion,
        string? healthTelemetry,
        string? changeValidation,
        string? rollbackPlan,
        string? riskSummary,
        string? recommendedNextAction,
        bool requiresUserApproval)
    {
        var missing = new List<string>();
        var proof = new List<ReleaseProofLink>
        {
            CreateProof("source_change", "Source change", sourceChange, missing),
            CreateProof("build", "Build pipeline", build, missing),
            CreateProof("artifact", "Artifact/version", artifactVersion ?? candidateVersion, missing),
            CreateProof("deployment", "Deployment", deployment, missing),
            CreateProof("runtime_version", "Runtime version", runtimeVersion, missing),
            CreateProof("health", "Scoped health telemetry", healthTelemetry, missing),
            CreateProof("change_validation", "Changed-behavior validation", changeValidation, missing),
            CreateProof("rollback", "Rollback path", rollbackPlan, missing)
        };

        if (string.IsNullOrWhiteSpace(targetScope))
            missing.Add("target scope");

        var packet = new ReleaseEvidencePacket
        {
            ChatId = chatId,
            Service = service.Trim(),
            Goal = goal.Trim(),
            TargetScope = targetScope?.Trim() ?? "",
            Candidate = new ReleaseCandidateEvidence
            {
                Version = candidateVersion?.Trim() ?? "",
                Rationale = missing.Count == 0
                    ? "Candidate has a complete proof chain."
                    : "Candidate is not definitively validated until missing proof is supplied."
            },
            ProofChain = proof,
            MissingProof = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RecommendedNextAction = string.IsNullOrWhiteSpace(recommendedNextAction)
                ? (missing.Count == 0 ? "Present the draft for explicit approval before any risky action." : "Collect missing proof before requesting approval.")
                : recommendedNextAction.Trim(),
            RequiresUserApproval = requiresUserApproval,
            Risk = new ReleaseRiskAssessment
            {
                Summary = riskSummary?.Trim() ?? "",
                Confidence = missing.Count == 0 ? 85 : 45,
                Signals = string.IsNullOrWhiteSpace(riskSummary) ? [] : [riskSummary.Trim()]
            },
            State = new ReleaseStateSnapshot
            {
                CandidateState = string.IsNullOrWhiteSpace(candidateVersion) ? "Missing candidate version" : "Candidate identified",
                DeploymentState = string.IsNullOrWhiteSpace(deployment) ? "Missing deployment proof" : "Deployment evidence captured",
                HealthState = string.IsNullOrWhiteSpace(healthTelemetry) ? "Missing scoped health telemetry" : "Scoped health captured",
                ChangeValidationState = string.IsNullOrWhiteSpace(changeValidation) ? "Missing changed-behavior proof" : "Change validation captured",
                ApprovalState = requiresUserApproval ? "Approval required before risky action" : "No risky action requested"
            }
        };

        _dataStore.Data.ReleaseEvidencePackets.Add(packet);
        _dataStore.Save();
        return FormatEvidencePacket(packet);
    }

    private string ReviewEvidencePacket(string evidencePacketId)
    {
        if (!Guid.TryParse(evidencePacketId, out var id))
            return $"Invalid release evidence packet ID: {evidencePacketId}";

        var packet = _dataStore.Data.ReleaseEvidencePackets.FirstOrDefault(item => item.Id == id);
        return packet is null
            ? $"Release evidence packet not found: {evidencePacketId}"
            : FormatEvidencePacket(packet);
    }

    private string ListEvidencePackets(Guid chatId, int maxItems, bool includeAllChats)
    {
        var packets = _dataStore.Data.ReleaseEvidencePackets
            .Where(packet => includeAllChats || packet.ChatId == chatId)
            .OrderByDescending(packet => packet.UpdatedAt)
            .ThenByDescending(packet => packet.CreatedAt)
            .Take(NormalizeListLimit(maxItems))
            .ToList();

        if (packets.Count == 0)
            return includeAllChats
                ? "release_evidence_packets: none"
                : "release_evidence_packets: none for this chat";

        var builder = new StringBuilder();
        builder.AppendLine("release_evidence_packets:");
        foreach (var packet in packets)
        {
            builder.AppendLine($"- evidence_packet_id: {packet.Id}");
            builder.AppendLine($"  service: {Blank(packet.Service)}");
            builder.AppendLine($"  goal: {Blank(packet.Goal)}");
            builder.AppendLine($"  candidate: {Blank(packet.Candidate?.Version)}");
            builder.AppendLine($"  target_scope: {Blank(packet.TargetScope)}");
            builder.AppendLine($"  missing_proof_count: {packet.MissingProof.Count}");
            builder.AppendLine($"  safefly_draft_id: {Blank(packet.SafeFlyDraftId?.ToString())}");
            builder.AppendLine($"  updated_at: {packet.UpdatedAt:O}");
        }

        return builder.ToString().Trim();
    }

    private static ReleaseProofLink CreateProof(string type, string label, string? evidence, List<string> missing)
    {
        var hasEvidence = !string.IsNullOrWhiteSpace(evidence);
        if (!hasEvidence)
            missing.Add(label);

        return new ReleaseProofLink
        {
            LinkType = type,
            Label = label,
            Evidence = evidence?.Trim() ?? "",
            Status = hasEvidence ? "Captured" : "Missing"
        };
    }

    private static string FormatEvidencePacket(ReleaseEvidencePacket packet)
    {
        var status = packet.MissingProof.Count == 0 ? "definitively validated draft evidence" : "not definitively validated";
        var builder = new StringBuilder();
        builder.AppendLine($"status: {status}");
        builder.AppendLine($"evidence_packet_id: {packet.Id}");
        builder.AppendLine($"service: {packet.Service}");
        builder.AppendLine($"goal: {packet.Goal}");
        builder.AppendLine($"target_scope: {Blank(packet.TargetScope)}");
        builder.AppendLine($"candidate: {Blank(packet.Candidate?.Version)}");
        builder.AppendLine("proof_chain:");
        foreach (var link in packet.ProofChain)
            builder.AppendLine($"- {link.Label}: {link.Status} - {Blank(link.Evidence)}");
        builder.AppendLine("missing_proof: " + (packet.MissingProof.Count == 0 ? "none" : string.Join("; ", packet.MissingProof)));
        builder.AppendLine($"risk: {Blank(packet.Risk.Summary)}");
        builder.AppendLine($"confidence: {packet.Risk.Confidence}");
        builder.AppendLine($"recommended_next_action: {packet.RecommendedNextAction}");
        builder.AppendLine($"requires_user_approval: {packet.RequiresUserApproval}");
        return builder.ToString().Trim();
    }

    private string CreateSafeFlyDraft(
        Guid chatId,
        string? evidencePacketId,
        string? service,
        string? candidateVersion,
        string? targetScope,
        string? changeType,
        string? autoCreateCapability,
        string? riskSummary,
        string? validationSummary,
        string? healthSummary,
        string? bakeTimeSummary,
        string? rollbackPlan,
        string? communicationsPlan,
        string? reviewPathRecommendation,
        string? links)
    {
        var packet = FindEvidencePacket(evidencePacketId);
        var draft = new ReleaseSafeFlyDraft
        {
            ChatId = chatId,
            EvidencePacketId = packet?.Id,
            Service = FirstNonBlank(service, packet?.Service),
            CandidateVersion = FirstNonBlank(candidateVersion, packet?.Candidate?.Version),
            TargetScope = FirstNonBlank(targetScope, packet?.TargetScope),
            ChangeType = changeType?.Trim() ?? "",
            AutoCreateCapability = autoCreateCapability?.Trim() ?? "draft-only fallback",
            RiskSummary = FirstNonBlank(riskSummary, packet?.Risk.Summary),
            ValidationSummary = FirstNonBlank(validationSummary, GetProofEvidence(packet, "change_validation")),
            HealthSummary = FirstNonBlank(healthSummary, GetProofEvidence(packet, "health")),
            BakeTimeSummary = bakeTimeSummary?.Trim() ?? "",
            RollbackPlan = FirstNonBlank(rollbackPlan, GetProofEvidence(packet, "rollback")),
            CommunicationsPlan = communicationsPlan?.Trim() ?? "",
            ReviewPathRecommendation = reviewPathRecommendation?.Trim() ?? "draft only until required proof and approval are available",
            Links = SplitList(links)
        };

        ValidateDraftFields(draft);
        _dataStore.Data.ReleaseSafeFlyDrafts.Add(draft);
        if (packet is not null)
        {
            packet.SafeFlyDraftId = draft.Id;
            packet.UpdatedAt = DateTimeOffset.Now;
        }

        _dataStore.Save();
        return FormatDraft(draft);
    }

    private string ValidateSafeFlyDraft(string draftId)
    {
        if (!Guid.TryParse(draftId, out var id))
            return $"Invalid SafeFly/R2D draft ID: {draftId}";

        var draft = _dataStore.Data.ReleaseSafeFlyDrafts.FirstOrDefault(item => item.Id == id);
        if (draft is null)
            return $"SafeFly/R2D draft not found: {draftId}";

        ValidateDraftFields(draft);
        draft.UpdatedAt = DateTimeOffset.Now;
        _dataStore.Save();
        return FormatDraft(draft);
    }

    private string GetSafeFlyDraft(string draftId)
    {
        if (!Guid.TryParse(draftId, out var id))
            return $"Invalid SafeFly/R2D draft ID: {draftId}";

        var draft = _dataStore.Data.ReleaseSafeFlyDrafts.FirstOrDefault(item => item.Id == id);
        return draft is null
            ? $"SafeFly/R2D draft not found: {draftId}"
            : FormatDraft(draft);
    }

    private string ListSafeFlyDrafts(Guid chatId, int maxItems, bool includeAllChats)
    {
        var drafts = _dataStore.Data.ReleaseSafeFlyDrafts
            .Where(draft => includeAllChats || draft.ChatId == chatId)
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenByDescending(draft => draft.CreatedAt)
            .Take(NormalizeListLimit(maxItems))
            .ToList();

        if (drafts.Count == 0)
            return includeAllChats
                ? "r2d_drafts: none"
                : "r2d_drafts: none for this chat";

        var builder = new StringBuilder();
        builder.AppendLine("r2d_drafts:");
        foreach (var draft in drafts)
        {
            builder.AppendLine($"- r2d_draft_id: {draft.Id}");
            builder.AppendLine($"  evidence_packet_id: {Blank(draft.EvidencePacketId?.ToString())}");
            builder.AppendLine($"  service: {Blank(draft.Service)}");
            builder.AppendLine($"  candidate_version: {Blank(draft.CandidateVersion)}");
            builder.AppendLine($"  target_scope: {Blank(draft.TargetScope)}");
            builder.AppendLine($"  status: {draft.Status}");
            builder.AppendLine($"  missing_fields_count: {draft.MissingFields.Count}");
            builder.AppendLine($"  updated_at: {draft.UpdatedAt:O}");
        }

        return builder.ToString().Trim();
    }

    private static void ValidateDraftFields(ReleaseSafeFlyDraft draft)
    {
        var missing = new List<string>();
        AddMissingIfBlank(draft.Service, "service/component", missing);
        AddMissingIfBlank(draft.CandidateVersion, "candidate version", missing);
        AddMissingIfBlank(draft.TargetScope, "target scope", missing);
        AddMissingIfBlank(draft.ChangeType, "change type", missing);
        AddMissingIfBlank(draft.RiskSummary, "risk summary", missing);
        AddMissingIfBlank(draft.ValidationSummary, "validation proof", missing);
        AddMissingIfBlank(draft.HealthSummary, "health proof", missing);
        AddMissingIfBlank(draft.RollbackPlan, "rollback plan", missing);

        draft.MissingFields = missing;
        draft.Status = missing.Count == 0
            ? "Draft ready for explicit create/submit approval"
            : "Draft missing required metadata or proof";
    }

    private static void AddMissingIfBlank(string value, string label, List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(value))
            missing.Add(label);
    }

    private static string FormatDraft(ReleaseSafeFlyDraft draft)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"status: {draft.Status}");
        builder.AppendLine($"r2d_draft_id: {draft.Id}");
        builder.AppendLine($"service: {Blank(draft.Service)}");
        builder.AppendLine($"candidate_version: {Blank(draft.CandidateVersion)}");
        builder.AppendLine($"target_scope: {Blank(draft.TargetScope)}");
        builder.AppendLine($"change_type: {Blank(draft.ChangeType)}");
        builder.AppendLine($"evidence_packet_id: {Blank(draft.EvidencePacketId?.ToString())}");
        builder.AppendLine($"auto_create_capability: {Blank(draft.AutoCreateCapability)}");
        builder.AppendLine($"review_path: {Blank(draft.ReviewPathRecommendation)}");
        builder.AppendLine($"external_request_id: {Blank(draft.ExternalRequestId)}");
        builder.AppendLine($"request_status: {Blank(draft.RequestStatus)}");
        builder.AppendLine($"status_url: {Blank(draft.StatusUrl)}");
        builder.AppendLine("missing_fields: " + (draft.MissingFields.Count == 0 ? "none" : string.Join("; ", draft.MissingFields)));
        builder.AppendLine("links: " + (draft.Links.Count == 0 ? "none" : string.Join("; ", draft.Links)));
        builder.AppendLine("requires_user_approval_for_create: true");
        builder.AppendLine("requires_user_approval_for_submit: true");
        builder.AppendLine("note: This is a local draft only. No SafeFly/R2D request was created or submitted.");
        return builder.ToString().Trim();
    }

    private string DraftMonitorPrompt(string? evidencePacketId, string? service, string? statusUrl, string? wakeConditions)
    {
        var packet = FindEvidencePacket(evidencePacketId);
        var serviceName = FirstNonBlank(service, packet?.Service, "the release");
        var conditions = SplitList(wakeConditions);
        if (conditions.Count == 0)
        {
            conditions =
            [
                "SafeFly request enters waiting-for-info",
                "approval is granted or blocked",
                "review becomes stale",
                "stage completes or fails",
                "health degrades",
                "change validation becomes ready or fails",
                "user approval is needed for the next action"
            ];
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Monitor {serviceName} and wake this chat only when attention is useful.");
        if (!string.IsNullOrWhiteSpace(statusUrl))
            builder.AppendLine($"Status link: {statusUrl.Trim()}");
        if (packet is not null)
            builder.AppendLine($"Evidence packet: {packet.Id}");
        builder.AppendLine("Wake conditions:");
        foreach (var condition in conditions)
            builder.AppendLine("- " + condition);
        builder.AppendLine("When waking, include current state, evidence, missing proof, recommended next action, and whether explicit approval is required.");
        return builder.ToString().Trim();
    }

    private string CreateMonitorJob(
        Guid chatId,
        string? evidencePacketId,
        string? draftId,
        string? service,
        string? statusUrl,
        string? wakeConditions,
        int intervalMinutes,
        bool runNow)
    {
        if (!_dataStore.Data.Chats.Any(chat => chat.Id == chatId))
            return "release_monitor_job: not_created\nreason: linked chat was not found";

        var packet = FindEvidencePacket(evidencePacketId);
        var draft = FindSafeFlyDraft(draftId);
        var serviceName = FirstNonBlank(service, draft?.Service, packet?.Service, "release");
        var monitorPrompt = DraftMonitorPrompt(
            packet?.Id.ToString() ?? evidencePacketId,
            serviceName,
            FirstNonBlank(statusUrl, draft?.StatusUrl),
            wakeConditions);

        if (draft is not null)
            monitorPrompt += $"\nSafeFly/R2D draft: {draft.Id}";

        var now = DateTimeOffset.Now;
        var job = new BackgroundJob
        {
            ChatId = chatId,
            Name = BuildUniqueJobName($"Release monitor - {serviceName}"),
            Description = $"Monitor release state for {serviceName}.",
            Prompt = monitorPrompt,
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Interval,
            IntervalMinutes = Math.Clamp(intervalMinutes, 1, 525_600),
            IsEnabled = true,
            IsTemporary = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        BackgroundJobSchedule.Normalize(job);
        job.NextRunAt = runNow ? now : BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false);

        _dataStore.AddBackgroundJob(job);
        SaveReleaseData();

        return $"""
            release_monitor_job: created
            job_id: {job.Id}
            name: {job.Name}
            service: {serviceName}
            interval_minutes: {job.IntervalMinutes}
            next_run_at: {job.NextRunAt:O}
            evidence_packet_id: {Blank(packet?.Id.ToString())}
            r2d_draft_id: {Blank(draft?.Id.ToString())}
            """;
    }

    private string DiscoverR2dCapabilities(string repoPath)
    {
        var keywords = new[] { "r2d", "safefly", "safe-fly", "autocreate", "auto-create", "release" };
        var repoFiles = Directory.Exists(repoPath) ? FindCapabilityFileMatches(repoPath, keywords) : [];
        var workspaceMcp = Directory.Exists(repoPath) ? FindWorkspaceMcpServerMatches(repoPath, keywords) : [];
        var configuredMcp = FindConfiguredMcpMatches(keywords);

        var builder = new StringBuilder();
        builder.AppendLine(Directory.Exists(repoPath)
            ? "status: r2d capability discovery complete"
            : "status: repo path missing; only configured Lumi MCPs were checked");
        builder.AppendLine($"repo_path: {repoPath}");
        AppendCapabilitySection(builder, "repo_r2d_or_safefly_files", repoFiles);
        AppendCapabilitySection(builder, "workspace_r2d_or_safefly_mcp_servers", workspaceMcp);
        AppendCapabilitySection(builder, "configured_lumi_r2d_or_safefly_mcp_servers", configuredMcp);
        builder.AppendLine("recommended_next_tool_path: use repo AutoCreate capability if present; otherwise create a local r2d_create_draft, validate it, and use r2d_prepare_approved_request only after exact user approval.");
        builder.AppendLine("submission_policy: built-in Release Manager tools never submit external R2D/SafeFly requests by themselves.");
        return builder.ToString().Trim();
    }

    private string DiscoverEvidenceAdapters(string repoPath)
    {
        var buildKeywords = new[] { "ado", "azure-devops", "azure devops", "pipeline", "build", "artifact" };
        var deploymentKeywords = new[] { "ev2", "deployment", "release-analysis", "release analysis", "stage", "rollout" };
        var telemetryKeywords = new[] { "kusto", "adx", "geneva", "telemetry", "health", "dashboard", "slo", "sli" };
        var validationKeywords = new[] { "validation", "change-validation", "smoke", "e2e", "probe", "runtime" };

        var builder = new StringBuilder();
        builder.AppendLine(Directory.Exists(repoPath)
            ? "status: evidence adapter discovery complete"
            : "status: repo path missing; only configured Lumi MCPs were checked");
        builder.AppendLine($"repo_path: {repoPath}");
        AppendEvidenceAdapterSection(builder, "build_run_artifact_adapters", repoPath, buildKeywords);
        AppendEvidenceAdapterSection(builder, "deployment_stage_runtime_adapters", repoPath, deploymentKeywords);
        AppendEvidenceAdapterSection(builder, "health_telemetry_adapters", repoPath, telemetryKeywords);
        AppendEvidenceAdapterSection(builder, "change_validation_adapters", repoPath, validationKeywords);
        builder.AppendLine("recommended_next_tool_path: invoke the best matching repo/MCP adapter for each proof-chain link, then persist findings with release_build_evidence_packet.");
        builder.AppendLine("proof_policy: broad service health is context only; runtime version and changed-behavior validation must be scoped to the exact target.");
        return builder.ToString().Trim();
    }

    private void AppendEvidenceAdapterSection(StringBuilder builder, string title, string repoPath, string[] keywords)
    {
        var matches = new List<string>();
        if (Directory.Exists(repoPath))
        {
            matches.AddRange(FindCapabilityFileMatches(repoPath, keywords));
            matches.AddRange(FindWorkspaceMcpServerMatches(repoPath, keywords));
        }

        matches.AddRange(FindConfiguredMcpMatches(keywords));
        AppendCapabilitySection(builder, title, matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private string DraftEvidenceCollectionPlan(
        string service,
        string targetScope,
        string? candidateVersion,
        string? sourceChange,
        string? changedComponents,
        string? directDependencies,
        string? healthHints)
    {
        var changed = SplitList(changedComponents);
        var dependencies = SplitList(directDependencies);
        var builder = new StringBuilder();
        builder.AppendLine("evidence_collection_plan:");
        builder.AppendLine($"service: {service.Trim()}");
        builder.AppendLine($"target_scope: {targetScope.Trim()}");
        builder.AppendLine($"candidate_version: {Blank(candidateVersion)}");
        builder.AppendLine($"source_change_hint: {Blank(sourceChange)}");
        builder.AppendLine("proof_chain_steps:");
        builder.AppendLine("- source_change: confirm exact commit, branch, PRs, work items, and changed files.");
        builder.AppendLine("- build_artifact: query ADO/build adapter for run ID, branch, result, tests, artifact name, and exact artifact version.");
        builder.AppendLine("- deployment_stage: query EV2/release-analysis/deployment adapter for exact stage/ring/environment/region/stamp and deployment time.");
        builder.AppendLine("- runtime_version: confirm the target scope is actually running the candidate version, not just that deployment succeeded.");
        builder.AppendLine("- health_telemetry: run Kusto/ADX/Geneva/dashboard queries filtered by candidate version, scope, and deployment time window.");
        builder.AppendLine("- change_validation: validate newly changed behavior or code paths; service-up is not sufficient.");
        builder.AppendLine("- rollback: identify previous good version and exact rollback path before recommending risky action.");
        builder.AppendLine("smallest_safe_scope:");
        builder.AppendLine("- include changed components: " + (changed.Count == 0 ? "(need changed component list)" : string.Join("; ", changed)));
        builder.AppendLine("- include direct dependencies: " + (dependencies.Count == 0 ? "(none provided; discover direct runtime dependencies)" : string.Join("; ", dependencies)));
        builder.AppendLine("- exclude unrelated services, broad regions, and stamps not touched by the change.");
        builder.AppendLine("health_hints: " + Blank(healthHints));
        builder.AppendLine("persist_with: release_build_evidence_packet");
        builder.AppendLine("missing_proof_policy: if any link cannot be proven for the exact scope/version/time window, mark the release not definitively validated.");
        return builder.ToString().Trim();
    }

    private string PrepareApprovedR2dRequest(
        string draftId,
        string approvedAction,
        string approvedScope,
        string approvedVersion,
        string approvedRisk,
        string? externalCapability)
    {
        var draft = FindSafeFlyDraft(draftId);
        if (draft is null)
            return $"SafeFly/R2D draft not found: {draftId}";

        ValidateDraftFields(draft);
        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(approvedAction))
            blockers.Add("exact approved action");
        if (!ExactMatch(approvedScope, draft.TargetScope))
            blockers.Add("approved scope must match draft target scope");
        if (!ExactMatch(approvedVersion, draft.CandidateVersion))
            blockers.Add("approved version must match draft candidate version");
        if (string.IsNullOrWhiteSpace(approvedRisk))
            blockers.Add("exact risk acknowledgement");
        if (draft.MissingFields.Count > 0)
            blockers.Add("draft still has missing fields: " + string.Join("; ", draft.MissingFields));
        if (string.IsNullOrWhiteSpace(externalCapability) ||
            externalCapability.Contains("draft-only", StringComparison.OrdinalIgnoreCase))
            blockers.Add("external AutoCreate capability");

        if (blockers.Count > 0)
        {
            draft.Status = "Approval gate blocked";
            draft.UpdatedAt = DateTimeOffset.Now;
            SaveReleaseData();
            return $"""
                approval_gate_status: blocked
                r2d_draft_id: {draft.Id}
                missing_approval_or_integration: {string.Join("; ", blockers)}
                no_request_submitted: true
                """;
        }

        draft.AutoCreateCapability = externalCapability!.Trim();
        draft.RequestStatus = "Ready for approved external AutoCreate handoff";
        draft.Status = "Ready for approved external AutoCreate handoff";
        draft.LastStatusSummary = $"User approved action '{approvedAction.Trim()}' for exact scope/version/risk. Hand off to {draft.AutoCreateCapability}.";
        draft.LastStatusCheckedAt = DateTimeOffset.Now;
        draft.UpdatedAt = DateTimeOffset.Now;
        SaveReleaseData();

        return $"""
            approval_gate_status: ready_for_external_handoff
            r2d_draft_id: {draft.Id}
            external_capability: {draft.AutoCreateCapability}
            approved_action: {approvedAction.Trim()}
            approved_scope: {approvedScope.Trim()}
            approved_version: {approvedVersion.Trim()}
            approved_risk: {approvedRisk.Trim()}
            no_request_submitted_by_lumi_helper: true
            next_step: invoke the approved external AutoCreate capability with this draft, then record status with r2d_update_request_status.
            """;
    }

    private string UpdateR2dRequestStatus(
        string draftId,
        string? requestId,
        string? requestStatus,
        string? statusUrl,
        string? summary)
    {
        var draft = FindSafeFlyDraft(draftId);
        if (draft is null)
            return $"SafeFly/R2D draft not found: {draftId}";

        if (string.IsNullOrWhiteSpace(requestId) &&
            string.IsNullOrWhiteSpace(requestStatus) &&
            string.IsNullOrWhiteSpace(statusUrl) &&
            string.IsNullOrWhiteSpace(summary))
            return "No R2D/SafeFly status changes were provided.";

        if (!string.IsNullOrWhiteSpace(requestId))
            draft.ExternalRequestId = requestId.Trim();
        if (!string.IsNullOrWhiteSpace(requestStatus))
        {
            draft.RequestStatus = requestStatus.Trim();
            draft.Status = "External request " + requestStatus.Trim();
        }
        if (!string.IsNullOrWhiteSpace(statusUrl))
            draft.StatusUrl = statusUrl.Trim();
        if (!string.IsNullOrWhiteSpace(summary))
            draft.LastStatusSummary = summary.Trim();

        draft.LastStatusCheckedAt = DateTimeOffset.Now;
        draft.UpdatedAt = DateTimeOffset.Now;
        SaveReleaseData();
        return GetR2dRequestStatus(draft.Id.ToString());
    }

    private string GetR2dRequestStatus(string draftId)
    {
        var draft = FindSafeFlyDraft(draftId);
        if (draft is null)
            return $"SafeFly/R2D draft not found: {draftId}";

        return $"""
            r2d_request_status:
            r2d_draft_id: {draft.Id}
            external_request_id: {Blank(draft.ExternalRequestId)}
            request_status: {Blank(draft.RequestStatus)}
            status_url: {Blank(draft.StatusUrl)}
            last_status_summary: {Blank(draft.LastStatusSummary)}
            last_status_checked_at: {draft.LastStatusCheckedAt:O}
            local_draft_status: {draft.Status}
            """;
    }

    private string DiscoverWorkIqCapabilities(string repoPath)
    {
        var keywords = new[] { "workiq", "work-iq", "work iq", "communications", "communication", "context" };
        var workspaceMcp = Directory.Exists(repoPath) ? FindWorkspaceMcpServerMatches(repoPath, keywords) : [];
        var configuredMcp = FindConfiguredMcpMatches(keywords);

        var builder = new StringBuilder();
        builder.AppendLine("workiq_capabilities:");
        AppendCapabilitySection(builder, "workspace_workiq_mcp_servers", workspaceMcp);
        AppendCapabilitySection(builder, "configured_lumi_workiq_mcp_servers", configuredMcp);
        builder.AppendLine("required_servers: workiq-context, workiq-communications");
        builder.AppendLine("onboarding_policy: when onboarding a new project, attempt Work IQ setup and MCP installation/activation before release planning; record any missing or blocked setup.");
        builder.AppendLine("send_policy: draft communications from release evidence, but never send Teams/email without exact user approval for audience, message, scope, version, and risk.");
        return builder.ToString().Trim();
    }

    private string DraftReleaseCommunications(
        string? evidencePacketId,
        string? draftId,
        string? messageKind,
        string? audience,
        string? additionalContext)
    {
        var packet = FindEvidencePacket(evidencePacketId);
        var draft = FindSafeFlyDraft(draftId);
        var service = FirstNonBlank(draft?.Service, packet?.Service, "release");
        var version = FirstNonBlank(draft?.CandidateVersion, packet?.Candidate?.Version);
        var scope = FirstNonBlank(draft?.TargetScope, packet?.TargetScope);
        var risk = FirstNonBlank(draft?.RiskSummary, packet?.Risk.Summary);
        var validation = FirstNonBlank(draft?.ValidationSummary, GetProofEvidence(packet, "change_validation"));
        var health = FirstNonBlank(draft?.HealthSummary, GetProofEvidence(packet, "health"));
        var r2dState = FirstNonBlank(draft?.RequestStatus, draft?.Status, packet?.State.R2DState);

        return $"""
            communication_draft:
            kind: {FirstNonBlank(messageKind, "release status update")}
            audience: {Blank(audience)}
            service: {service}
            version: {Blank(version)}
            scope: {Blank(scope)}
            health: {Blank(health)}
            change_validation: {Blank(validation)}
            r2d_safefly_state: {Blank(r2dState)}
            risk: {Blank(risk)}
            recommended_next_action: {Blank(packet?.RecommendedNextAction)}
            links: {FormatDraftLinks(draft)}
            additional_context: {Blank(additionalContext)}

            Draft message:
            Hi, quick release update for {service}: version {Blank(version)} is scoped to {Blank(scope)}. Health is {Blank(health)}. Change validation is {Blank(validation)}. R2D/SafeFly state is {Blank(r2dState)}. Risk summary: {Blank(risk)}. Recommended next action: {Blank(packet?.RecommendedNextAction)}.

            send_blocked_until_explicit_approval: true
            approval_required_for: exact audience, exact message, exact scope, exact version, and risk acknowledgement
            """;
    }

    private string ImportRemaData(string? dataJsonPath, bool previewOnly)
    {
        var path = FirstNonBlank(dataJsonPath, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Rema",
            "data.json"));

        if (!File.Exists(path))
            return $"rema_import: file not found\npath: {path}";

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var profiles = ExtractRemaProfiles(document.RootElement);
            if (profiles.Count == 0)
                return $"rema_import: no service profiles found\npath: {path}";

            if (previewOnly)
                return FormatRemaImport("preview", path, profiles, importedCount: 0, skippedCount: 0);

            var imported = 0;
            var skipped = 0;
            foreach (var profile in profiles)
            {
                if (HasReleaseProfile(profile))
                {
                    skipped++;
                    continue;
                }

                _dataStore.Data.ReleaseServiceProfiles.Add(profile);
                imported++;
            }

            if (imported > 0)
                SaveReleaseData();

            return FormatRemaImport("imported", path, profiles, imported, skipped);
        }
        catch (JsonException ex)
        {
            return $"rema_import: malformed JSON\npath: {path}\nerror: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"rema_import: could not read\npath: {path}\nerror: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"rema_import: access denied\npath: {path}\nerror: {ex.Message}";
        }
    }

    private static string DraftNextActionQueue(
        string service,
        string? candidateState,
        string? pipelineState,
        string? deploymentState,
        string? healthState,
        string? changeValidationState,
        string? r2dState,
        string? approvalState)
    {
        var items = new List<(int Priority, string Reason, string Action)>
        {
            (1, healthState ?? "", "Pause or mitigate if health is degraded; gather scoped telemetry before progressing."),
            (2, deploymentState ?? "", "Resolve failed or uncertain deployment state before review or promotion."),
            (3, approvalState ?? "", "Ask for explicit approval only after exact action, scope, version, and risk are clear."),
            (4, changeValidationState ?? "", "Validate newly changed behavior; service-up is not enough. If validation exposes a code issue, find the changed code owner and suggest Teams/email outreach."),
            (5, pipelineState ?? "", "Unblock failed or stale pipeline dependencies."),
            (6, r2dState ?? "", "Update R2D/SafeFly evidence or respond to waiting-for-info."),
            (7, candidateState ?? "", "Clarify candidate identity if version/build/branch is ambiguous.")
        };

        var relevant = items
            .Where(item => IsActionableReleaseState(item.Reason))
            .OrderBy(item => item.Priority)
            .ToList();

        if (relevant.Count == 0)
            relevant.Add((9, "No active blockers provided", "Build or refresh the definitive proof chain."));

        var builder = new StringBuilder();
        builder.AppendLine($"active_release_summary: {service}");
        builder.AppendLine("priority_queue:");
        foreach (var item in relevant)
        {
            builder.AppendLine($"- priority: {item.Priority}");
            builder.AppendLine($"  state: {item.Reason.Trim()}");
            builder.AppendLine($"  recommended_next_action: {item.Action}");
        }
        return builder.ToString().Trim();
    }

    private static bool IsActionableReleaseState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return false;

        var normalized = state.Trim().ToLowerInvariant();
        return normalized is not ("healthy" or "ok" or "green" or "succeeded" or "completed" or "approved" or "validated" or "ready" or "no blockers" or "no issues");
    }

    private string BuildUniqueJobName(string baseName)
    {
        var normalizedBaseName = baseName.Trim();
        var existingNames = _dataStore.SnapshotBackgroundJobs()
            .Select(static job => job.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(normalizedBaseName))
            return normalizedBaseName;

        for (var index = 2; ; index++)
        {
            var candidate = $"{normalizedBaseName} ({index})";
            if (!existingNames.Contains(candidate))
                return candidate;
        }
    }

    private List<string> FindConfiguredMcpMatches(string[] keywords)
    {
        return _dataStore.Data.McpServers
            .Where(server => server.IsEnabled && (MatchesAny(server.Name, keywords) || server.Tools.Any(tool => MatchesAny(tool, keywords))))
            .Select(server =>
            {
                var tools = server.Tools
                    .Where(tool => MatchesAny(tool, keywords))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return tools.Count == 0
                    ? server.Name
                    : $"{server.Name}: {string.Join(", ", tools)}";
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> FindCapabilityFileMatches(string repoPath, string[] keywords)
    {
        var matches = new List<string>();
        foreach (var directory in CapabilityDirectories)
        {
            var fullPath = Path.Combine(repoPath, directory);
            if (!Directory.Exists(fullPath))
                continue;

            foreach (var file in Directory.GetFiles(fullPath))
            {
                var fileName = Path.GetFileName(file);
                if (MatchesAny(fileName, keywords))
                    matches.Add(Path.Combine(directory, fileName));
            }
        }

        foreach (var file in CapabilityFiles)
        {
            if (MatchesAny(file, keywords) && File.Exists(Path.Combine(repoPath, file)))
                matches.Add(file);
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> FindWorkspaceMcpServerMatches(string repoPath, string[] keywords)
    {
        var matches = new List<string>();
        foreach (var file in CapabilityFiles.Where(static file => file.Contains("mcp", StringComparison.OrdinalIgnoreCase)))
        {
            var fullPath = Path.Combine(repoPath, file);
            if (!File.Exists(fullPath))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
                foreach (var server in ReadMcpServerNames(document.RootElement).Where(name => MatchesAny(name, keywords)))
                    matches.Add($"{file}: {server}");
            }
            catch (JsonException)
            {
                matches.Add($"{file}: malformed JSON");
            }
            catch (IOException)
            {
                matches.Add($"{file}: could not read");
            }
            catch (UnauthorizedAccessException)
            {
                matches.Add($"{file}: access denied");
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesAny(string value, string[] keywords)
        => keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool ExactMatch(string provided, string expected)
        => !string.IsNullOrWhiteSpace(provided)
           && !string.IsNullOrWhiteSpace(expected)
           && string.Equals(provided.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private ReleaseSafeFlyDraft? FindSafeFlyDraft(string? draftId)
    {
        if (!Guid.TryParse(draftId, out var id))
            return null;

        return _dataStore.Data.ReleaseSafeFlyDrafts.FirstOrDefault(item => item.Id == id);
    }

    private static string FormatDraftLinks(ReleaseSafeFlyDraft? draft)
        => draft is null || draft.Links.Count == 0 ? "none" : string.Join("; ", draft.Links);

    private bool HasReleaseProfile(ReleaseServiceProfile profile)
    {
        return _dataStore.Data.ReleaseServiceProfiles.Any(existing =>
            existing.ServiceName.Equals(profile.ServiceName, StringComparison.OrdinalIgnoreCase)
            && existing.RepoPath.Equals(profile.RepoPath, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ReleaseServiceProfile> ExtractRemaProfiles(JsonElement root)
    {
        var profiles = new List<ReleaseServiceProfile>();
        foreach (var element in EnumerateRemaProfileElements(root))
        {
            if (TryCreateRemaProfile(element, out var profile))
                profiles.Add(profile);
        }

        return profiles;
    }

    private static IEnumerable<JsonElement> EnumerateRemaProfileElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "releaseServiceProfiles", "serviceProfiles", "services", "projects" })
        {
            if (!TryGetProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    private static bool TryCreateRemaProfile(JsonElement element, out ReleaseServiceProfile profile)
    {
        profile = new ReleaseServiceProfile();
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var serviceName = FirstNonBlank(
            ReadString(element, "serviceName"),
            ReadString(element, "service"),
            ReadString(element, "name"),
            ReadString(element, "title"));
        if (string.IsNullOrWhiteSpace(serviceName))
            return false;

        profile = new ReleaseServiceProfile
        {
            ServiceName = serviceName,
            RepoPath = FirstNonBlank(ReadString(element, "repoPath"), ReadString(element, "repositoryPath"), ReadString(element, "workingDirectory")),
            AdoOrgUrl = FirstNonBlank(ReadString(element, "adoOrgUrl"), ReadString(element, "organizationUrl"), ReadString(element, "adoOrganization")),
            AdoProjectName = FirstNonBlank(ReadString(element, "adoProjectName"), ReadString(element, "projectName"), ReadString(element, "adoProject")),
            Pipelines = ReadRemaPipelines(element),
            HealthQueries = ReadRemaHealthQueries(element),
            Dependencies = ReadRemaDependencies(element)
        };
        return true;
    }

    private static List<ReleasePipelineConfig> ReadRemaPipelines(JsonElement element)
    {
        var array = ReadArray(element, "pipelines", "pipelineConfigs", "releasePipelines", "deploymentPipelines");
        if (array is null)
            return [];

        return array.Value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new ReleasePipelineConfig
            {
                Name = FirstNonBlank(ReadString(item, "name"), ReadString(item, "pipelineName")),
                PipelineId = FirstNonBlank(ReadString(item, "pipelineId"), ReadString(item, "id")),
                PipelineType = FirstNonBlank(ReadString(item, "pipelineType"), ReadString(item, "type")),
                Stage = ReadString(item, "stage"),
                Ring = ReadString(item, "ring"),
                Environment = FirstNonBlank(ReadString(item, "environment"), ReadString(item, "env")),
                Region = ReadString(item, "region"),
                Stamp = ReadString(item, "stamp")
            })
            .ToList();
    }

    private static List<ReleaseHealthQuery> ReadRemaHealthQueries(JsonElement element)
    {
        var array = ReadArray(element, "healthQueries", "queries", "dashboards");
        if (array is null)
            return [];

        return array.Value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new ReleaseHealthQuery
            {
                Name = FirstNonBlank(ReadString(item, "name"), ReadString(item, "title")),
                Query = FirstNonBlank(ReadString(item, "query"), ReadString(item, "kusto"), ReadString(item, "kql")),
                Scope = ReadString(item, "scope"),
                DashboardUrl = FirstNonBlank(ReadString(item, "dashboardUrl"), ReadString(item, "url"))
            })
            .ToList();
    }

    private static List<ReleasePipelineDependency> ReadRemaDependencies(JsonElement element)
    {
        var array = ReadArray(element, "dependencies", "pipelineDependencies");
        if (array is null)
            return [];

        return array.Value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new ReleasePipelineDependency
            {
                Source = ReadString(item, "source"),
                Target = ReadString(item, "target"),
                Reason = ReadString(item, "reason")
            })
            .ToList();
    }

    private static JsonElement? ReadArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        return null;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => ""
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string FormatRemaImport(
        string status,
        string path,
        List<ReleaseServiceProfile> profiles,
        int importedCount,
        int skippedCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"rema_import: {status}");
        builder.AppendLine($"path: {path}");
        builder.AppendLine($"profiles_found: {profiles.Count}");
        builder.AppendLine($"profiles_imported: {importedCount}");
        builder.AppendLine($"profiles_skipped_as_duplicates: {skippedCount}");
        builder.AppendLine("imported_scope: release service profiles only; dashboard and shift UX are intentionally not imported");
        builder.AppendLine("profiles:");
        foreach (var profile in profiles.OrderBy(static profile => profile.ServiceName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- service: {profile.ServiceName}");
            builder.AppendLine($"  repo_path: {Blank(profile.RepoPath)}");
            builder.AppendLine($"  pipelines: {profile.Pipelines.Count}");
            builder.AppendLine($"  health_queries: {profile.HealthQueries.Count}");
            builder.AppendLine($"  dependencies: {profile.Dependencies.Count}");
        }

        return builder.ToString().Trim();
    }

    private void SaveReleaseData()
    {
        _dataStore.Save();
        _releaseDataChanged?.Invoke();
    }

    private static int NormalizeListLimit(int maxItems)
        => Math.Clamp(maxItems, 1, 20);

    private ReleaseEvidencePacket? FindEvidencePacket(string? evidencePacketId)
    {
        if (!Guid.TryParse(evidencePacketId, out var id))
            return null;

        return _dataStore.Data.ReleaseEvidencePackets.FirstOrDefault(item => item.Id == id);
    }

    private static string GetProofEvidence(ReleaseEvidencePacket? packet, string linkType)
        => packet?.ProofChain.FirstOrDefault(link => link.LinkType == linkType)?.Evidence ?? "";

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(missing)" : value.Trim();
}
