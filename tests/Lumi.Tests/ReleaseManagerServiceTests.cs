using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class ReleaseManagerServiceTests
{
    [Fact]
    public void EnsureReleaseManagerLumi_CreatesBuiltInAgentWithApprovalGates()
    {
        var data = new AppData();

        var changed = ReleaseManagerService.EnsureReleaseManagerLumi(data);

        Assert.True(changed);
        var agent = Assert.Single(data.Agents);
        Assert.Equal(ReleaseManagerService.AgentName, agent.Name);
        Assert.True(agent.IsBuiltIn);
        Assert.Contains("R2D/SafeFly V2", agent.SystemPrompt);
        Assert.Contains("not definitively validated", agent.SystemPrompt);
        Assert.Contains("attempt Work IQ setup plus MCP installation and activation", agent.SystemPrompt);
        Assert.Contains("identify the code owner", agent.SystemPrompt);
        Assert.Contains("Never do these without explicit user approval", agent.SystemPrompt);
    }

    [Fact]
    public void BuildTools_ExposesDraftOnlyReleaseHelpers()
    {
        var service = new ReleaseManagerService(new DataStore(new AppData()));

        var tools = service.BuildTools(Guid.NewGuid(), Directory.GetCurrentDirectory());

        Assert.Contains(tools, tool => tool.Name == "release_discover_capabilities");
        Assert.Contains(tools, tool => tool.Name == "release_build_evidence_packet");
        Assert.Contains(tools, tool => tool.Name == "release_list_evidence_packets");
        Assert.Contains(tools, tool => tool.Name == "release_discover_evidence_adapters");
        Assert.Contains(tools, tool => tool.Name == "release_draft_evidence_collection_plan");
        Assert.Contains(tools, tool => tool.Name == "r2d_discover_capabilities");
        Assert.Contains(tools, tool => tool.Name == "r2d_create_draft");
        Assert.Contains(tools, tool => tool.Name == "r2d_get_draft");
        Assert.Contains(tools, tool => tool.Name == "r2d_list_drafts");
        Assert.Contains(tools, tool => tool.Name == "r2d_prepare_approved_request");
        Assert.Contains(tools, tool => tool.Name == "r2d_update_request_status");
        Assert.Contains(tools, tool => tool.Name == "release_create_monitor_job");
        Assert.Contains(tools, tool => tool.Name == "release_draft_comms");
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("submit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverCapabilities_ReportsMalformedCapabilityJson()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-release-capability-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            File.WriteAllText(Path.Combine(tempRoot, "capabilities.json"), "{ bad json");

            var service = new ReleaseManagerService(new DataStore(new AppData()));
            var tool = service.BuildTools(Guid.NewGuid(), tempRoot)
                .Single(tool => tool.Name == "release_discover_capabilities");

            var result = (await tool.InvokeAsync())?.ToString() ?? "";

            Assert.Contains("capabilities.json", result);
            Assert.Contains("malformed JSON", result);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_ReturnsRankedSectionsAndMcpServerNames()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-release-capability-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, ".github", "agents"));
            Directory.CreateDirectory(Path.Combine(tempRoot, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(tempRoot, ".github", "prompts"));
            Directory.CreateDirectory(Path.Combine(tempRoot, ".vscode"));

            File.WriteAllText(Path.Combine(tempRoot, ".github", "agents", "release-agent.md"), "agent");
            File.WriteAllText(Path.Combine(tempRoot, ".github", "skills", "health-skill.md"), "skill");
            File.WriteAllText(Path.Combine(tempRoot, ".github", "prompts", "r2d.prompt.md"), "prompt");
            File.WriteAllText(Path.Combine(tempRoot, "AGENTS.md"), "release guidance");
            File.WriteAllText(Path.Combine(tempRoot, "capabilities.json"), "{}");
            File.WriteAllText(Path.Combine(tempRoot, ".vscode", "mcp.json"), """
                {
                  "servers": {
                    "release-analysis": { "command": "release-analysis" },
                    "ado": { "command": "ado" }
                  }
                }
                """);

            var service = new ReleaseManagerService(new DataStore(new AppData()));
            var tool = service.BuildTools(Guid.NewGuid(), tempRoot)
                .Single(tool => tool.Name == "release_discover_capabilities");

            var result = (await tool.InvokeAsync())?.ToString() ?? "";

            Assert.Contains("status: discovered repo release capabilities", result);
            Assert.Contains("repo_agents_or_subagents:", result);
            Assert.Contains(@".github\agents: release-agent.md", result);
            Assert.Contains("repo_release_skills_or_prompts:", result);
            Assert.Contains(@".github\skills: health-skill.md", result);
            Assert.Contains(@".github\prompts: r2d.prompt.md", result);
            Assert.Contains("repo_mcp_configs:", result);
            Assert.Contains(@".vscode\mcp.json: ado, release-analysis", result);
            Assert.Contains("capability_manifests:", result);
            Assert.Contains("capabilities.json", result);
            Assert.Contains("general_instructions:", result);
            Assert.Contains("AGENTS.md", result);
            Assert.Contains("recommended_next_tool_path:", result);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DraftNextActionQueue_IgnoresExplicitlyHealthyCompletedStates()
    {
        var result = InvokeDraftNextActionQueue(
            service: "CheckoutService",
            candidateState: "ready",
            pipelineState: "succeeded",
            deploymentState: "completed",
            healthState: "healthy",
            changeValidationState: "validated",
            r2dState: "approved",
            approvalState: "approved");

        Assert.Contains("Build or refresh the definitive proof chain.", result);
        Assert.DoesNotContain("Pause or mitigate", result);
        Assert.DoesNotContain("Resolve failed or uncertain deployment state", result);
    }

    [Fact]
    public void DraftNextActionQueue_PrioritizesHealthBeforeDeploymentAndApprovalBlockers()
    {
        var result = InvokeDraftNextActionQueue(
            service: "CheckoutService",
            candidateState: null,
            pipelineState: null,
            deploymentState: "deployment failed in canary",
            healthState: "health degraded after candidate rollout",
            changeValidationState: null,
            r2dState: null,
            approvalState: "approval missing exact scope");

        var healthIndex = result.IndexOf("Pause or mitigate", StringComparison.Ordinal);
        var deploymentIndex = result.IndexOf("Resolve failed or uncertain deployment state", StringComparison.Ordinal);
        var approvalIndex = result.IndexOf("Ask for explicit approval", StringComparison.Ordinal);

        Assert.True(healthIndex >= 0);
        Assert.True(deploymentIndex > healthIndex);
        Assert.True(approvalIndex > deploymentIndex);
    }

    [Fact]
    public void DraftNextActionQueue_SuggestsOwnerOutreachForValidationIssues()
    {
        var result = InvokeDraftNextActionQueue(
            service: "CheckoutService",
            candidateState: null,
            pipelineState: null,
            deploymentState: null,
            healthState: null,
            changeValidationState: "checkout changed-path validation failed",
            r2dState: null,
            approvalState: null);

        Assert.Contains("find the changed code owner", result);
        Assert.Contains("Teams/email outreach", result);
    }

    [Fact]
    public void LocalReleaseWorkflow_CanResumeEvidenceAndDrafts()
    {
        var chatId = Guid.NewGuid();
        var data = new AppData();
        var service = new ReleaseManagerService(new DataStore(data));

        var evidence = InvokePrivateString(
            service,
            "CreateEvidencePacket",
            chatId,
            "CheckoutService",
            "R2D draft",
            "INT ring0 / westus / stamp-01",
            "2026.05.07.1",
            "commit abc123, PR 42, work item 1001",
            "build pipeline Checkout-CI run 9001 passed",
            "artifact checkout.zip version 2026.05.07.1",
            "deployment pipeline Checkout-Release stage INT completed at 2026-05-07T09:00Z",
            "runtime reports 2026.05.07.1 on stamp-01",
            "scoped health green for stamp-01 during deployment window",
            "checkout changed path smoke passed on stamp-01",
            "rollback to 2026.05.06.4 using Checkout-Release rollback stage",
            "low risk canary with direct rollback",
            "Create local SafeFly draft for explicit review",
            true);

        Assert.Contains("status: definitively validated draft evidence", evidence);
        Assert.Contains("missing_proof: none", evidence);

        var packet = Assert.Single(data.ReleaseEvidencePackets);
        var listEvidence = InvokePrivateString(service, "ListEvidencePackets", chatId, 5, false);
        Assert.Contains(packet.Id.ToString(), listEvidence);
        Assert.Contains("CheckoutService", listEvidence);

        var draft = InvokePrivateString(
            service,
            "CreateSafeFlyDraft",
            chatId,
            packet.Id.ToString(),
            null,
            null,
            null,
            "App Deployment",
            "repo AutoCreate skill",
            null,
            null,
            null,
            "30 minutes clean canary bake",
            null,
            "Draft reviewer summary only; do not send.",
            "async review",
            "https://build/9001; https://dashboard/stamp-01");

        Assert.Contains("status: Draft ready for explicit create/submit approval", draft);
        Assert.Contains("missing_fields: none", draft);
        Assert.Contains("note: This is a local draft only.", draft);

        var safeflyDraft = Assert.Single(data.ReleaseSafeFlyDrafts);
        Assert.Equal(safeflyDraft.Id, packet.SafeFlyDraftId);

        var fetchedDraft = InvokePrivateString(service, "GetSafeFlyDraft", safeflyDraft.Id.ToString());
        Assert.Contains(safeflyDraft.Id.ToString(), fetchedDraft);
        Assert.Contains(packet.Id.ToString(), fetchedDraft);
        Assert.Contains("https://build/9001", fetchedDraft);

        var validatedDraft = InvokePrivateString(service, "ValidateSafeFlyDraft", safeflyDraft.Id.ToString());
        Assert.Contains("missing_fields: none", validatedDraft);

        var listDrafts = InvokePrivateString(service, "ListSafeFlyDrafts", chatId, 5, false);
        Assert.Contains(safeflyDraft.Id.ToString(), listDrafts);
        Assert.Contains("missing_fields_count: 0", listDrafts);

        var monitorPrompt = InvokePrivateString(
            service,
            "DraftMonitorPrompt",
            packet.Id.ToString(),
            null,
            "https://safefly/requests/123",
            "stage completes; health degrades");

        Assert.Contains("Monitor CheckoutService", monitorPrompt);
        Assert.Contains(packet.Id.ToString(), monitorPrompt);
        Assert.Contains("https://safefly/requests/123", monitorPrompt);
        Assert.Contains("stage completes", monitorPrompt);
        Assert.Contains("explicit approval is required", monitorPrompt);
    }

    [Fact]
    public void CreateMonitorJob_CreatesLumiBackgroundJobFromReleaseEvidence()
    {
        var chatId = Guid.NewGuid();
        var data = new AppData
        {
            Chats =
            [
                new Chat
                {
                    Id = chatId,
                    Title = "Release chat"
                }
            ]
        };
        var releaseDataChanged = false;
        var service = new ReleaseManagerService(new DataStore(data), () => releaseDataChanged = true);
        var packet = CreateCompleteEvidencePacket(service, chatId);

        var result = InvokePrivateString(
            service,
            "CreateMonitorJob",
            chatId,
            packet.Id.ToString(),
            null,
            null,
            "https://safefly/requests/123",
            "stage completes; health degrades",
            5,
            true);

        Assert.Contains("release_monitor_job: created", result);
        Assert.True(releaseDataChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(chatId, job.ChatId);
        Assert.Equal("Release monitor - CheckoutService", job.Name);
        Assert.Equal(BackgroundJobTriggerTypes.Time, job.TriggerType);
        Assert.Equal(BackgroundJobScheduleTypes.Interval, job.ScheduleType);
        Assert.Equal(5, job.IntervalMinutes);
        Assert.False(job.IsTemporary);
        Assert.Contains(packet.Id.ToString(), job.Prompt);
        Assert.Contains("stage completes", job.Prompt);
        Assert.NotNull(job.NextRunAt);
    }

    [Fact]
    public void R2dApprovalGate_PreparesExternalHandoffOnlyAfterExactApproval()
    {
        var chatId = Guid.NewGuid();
        var data = new AppData();
        var service = new ReleaseManagerService(new DataStore(data));
        var packet = CreateCompleteEvidencePacket(service, chatId);
        var draft = CreateCompleteDraft(service, chatId, packet.Id);

        var blocked = InvokePrivateString(
            service,
            "PrepareApprovedR2dRequest",
            draft.Id.ToString(),
            "create SafeFly request",
            "wrong scope",
            draft.CandidateVersion,
            "low risk approved",
            "safefly-autocreate MCP");

        Assert.Contains("approval_gate_status: blocked", blocked);
        Assert.Contains("no_request_submitted: true", blocked);

        var ready = InvokePrivateString(
            service,
            "PrepareApprovedR2dRequest",
            draft.Id.ToString(),
            "create SafeFly request",
            draft.TargetScope,
            draft.CandidateVersion,
            "low risk approved",
            "safefly-autocreate MCP");

        Assert.Contains("approval_gate_status: ready_for_external_handoff", ready);
        Assert.Contains("no_request_submitted_by_lumi_helper: true", ready);
        Assert.Equal("Ready for approved external AutoCreate handoff", draft.Status);

        var updatedStatus = InvokePrivateString(
            service,
            "UpdateR2dRequestStatus",
            draft.Id.ToString(),
            "SF-123",
            "waiting-for-info",
            "https://safefly/requests/SF-123",
            "Reviewer requested scoped health link.");

        Assert.Contains("external_request_id: SF-123", updatedStatus);
        Assert.Contains("request_status: waiting-for-info", updatedStatus);
        Assert.Contains("Reviewer requested scoped health link.", updatedStatus);

        var fetchedStatus = InvokePrivateString(service, "GetR2dRequestStatus", draft.Id.ToString());
        Assert.Contains("https://safefly/requests/SF-123", fetchedStatus);
    }

    [Fact]
    public async Task R2dAndWorkIqDiscovery_FindRepoAndConfiguredMcpCapabilities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-release-capability-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(tempRoot, ".vscode"));
            File.WriteAllText(Path.Combine(tempRoot, ".github", "skills", "safefly-autocreate.md"), "skill");
            File.WriteAllText(Path.Combine(tempRoot, ".vscode", "mcp.json"), """
                {
                  "servers": {
                    "safefly-autocreate": { "command": "safefly" },
                    "workiq-communications": { "command": "workiq" }
                  }
                }
                """);

            var data = new AppData
            {
                McpServers =
                [
                    new McpServer
                    {
                        Name = "workiq-context",
                        Tools = ["workiq_context_lookup"]
                    },
                    new McpServer
                    {
                        Name = "release-safe-fly",
                        Tools = ["safefly_autocreate"]
                    }
                ]
            };
            var service = new ReleaseManagerService(new DataStore(data));
            var tools = service.BuildTools(Guid.NewGuid(), tempRoot);

            var r2d = (await tools.Single(tool => tool.Name == "r2d_discover_capabilities").InvokeAsync())?.ToString() ?? "";
            Assert.Contains(@".github\skills\safefly-autocreate.md", r2d);
            Assert.Contains(@".vscode\mcp.json: safefly-autocreate", r2d);
            Assert.Contains("release-safe-fly: safefly_autocreate", r2d);
            Assert.Contains("never submit external R2D/SafeFly", r2d);

            var workIq = (await tools.Single(tool => tool.Name == "release_discover_workiq_capabilities").InvokeAsync())?.ToString() ?? "";
            Assert.Contains(@".vscode\mcp.json: workiq-communications", workIq);
            Assert.Contains("workiq-context: workiq_context_lookup", workIq);
            Assert.Contains("attempt Work IQ setup and MCP installation/activation", workIq);
            Assert.Contains("never send Teams/email", workIq);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EvidenceAdapterDiscoveryAndPlan_CoverProofChainAdapters()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-release-evidence-adapter-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(tempRoot, ".vscode"));
            File.WriteAllText(Path.Combine(tempRoot, ".github", "skills", "kusto-health.md"), "skill");
            File.WriteAllText(Path.Combine(tempRoot, ".vscode", "mcp.json"), """
                {
                  "servers": {
                    "ado-pipelines": { "command": "ado" },
                    "release-analysis": { "command": "release-analysis" }
                  }
                }
                """);

            var data = new AppData
            {
                McpServers =
                [
                    new McpServer
                    {
                        Name = "adx-health",
                        Tools = ["kusto_query"]
                    }
                ]
            };
            var service = new ReleaseManagerService(new DataStore(data));
            var tools = service.BuildTools(Guid.NewGuid(), tempRoot);

            var discovery = (await tools.Single(tool => tool.Name == "release_discover_evidence_adapters").InvokeAsync())?.ToString() ?? "";
            Assert.Contains("build_run_artifact_adapters:", discovery);
            Assert.Contains(@".vscode\mcp.json: ado-pipelines", discovery);
            Assert.Contains("deployment_stage_runtime_adapters:", discovery);
            Assert.Contains(@".vscode\mcp.json: release-analysis", discovery);
            Assert.Contains("health_telemetry_adapters:", discovery);
            Assert.Contains("adx-health: kusto_query", discovery);

            var plan = InvokePrivateString(
                service,
                "DraftEvidenceCollectionPlan",
                "CheckoutService",
                "INT ring0 / westus / stamp-01",
                "2026.05.07.1",
                "commit abc123",
                "checkout api; fraud adapter",
                "Payments",
                "Canary dashboard");

            Assert.Contains("source_change:", plan);
            Assert.Contains("build_artifact:", plan);
            Assert.Contains("deployment_stage:", plan);
            Assert.Contains("runtime_version:", plan);
            Assert.Contains("health_telemetry:", plan);
            Assert.Contains("change_validation:", plan);
            Assert.Contains("checkout api; fraud adapter", plan);
            Assert.Contains("Payments", plan);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DraftReleaseCommunications_UsesEvidenceAndBlocksSending()
    {
        var chatId = Guid.NewGuid();
        var data = new AppData();
        var service = new ReleaseManagerService(new DataStore(data));
        var packet = CreateCompleteEvidencePacket(service, chatId);
        var draft = CreateCompleteDraft(service, chatId, packet.Id);

        var result = InvokePrivateString(
            service,
            "DraftReleaseCommunications",
            packet.Id.ToString(),
            draft.Id.ToString(),
            "stage completed",
            "Release review channel",
            "Canary bake is clean.");

        Assert.Contains("communication_draft:", result);
        Assert.Contains("CheckoutService", result);
        Assert.Contains("Release review channel", result);
        Assert.Contains("Canary bake is clean.", result);
        Assert.Contains("send_blocked_until_explicit_approval: true", result);
    }

    private static string InvokeDraftNextActionQueue(
        string service,
        string? candidateState,
        string? pipelineState,
        string? deploymentState,
        string? healthState,
        string? changeValidationState,
        string? r2dState,
        string? approvalState)
    {
        var method = typeof(ReleaseManagerService).GetMethod(
            "DraftNextActionQueue",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)method.Invoke(null,
            [
                service,
                candidateState,
                pipelineState,
                deploymentState,
                healthState,
                changeValidationState,
                r2dState,
                approvalState
            ])!;
    }

    private static string InvokePrivateString(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(instance, args)!;
    }

    private static ReleaseEvidencePacket CreateCompleteEvidencePacket(ReleaseManagerService service, Guid chatId)
    {
        InvokePrivateString(
            service,
            "CreateEvidencePacket",
            chatId,
            "CheckoutService",
            "R2D draft",
            "INT ring0 / westus / stamp-01",
            "2026.05.07.1",
            "commit abc123, PR 42, work item 1001",
            "build pipeline Checkout-CI run 9001 passed",
            "artifact checkout.zip version 2026.05.07.1",
            "deployment pipeline Checkout-Release stage INT completed at 2026-05-07T09:00Z",
            "runtime reports 2026.05.07.1 on stamp-01",
            "scoped health green for stamp-01 during deployment window",
            "checkout changed path smoke passed on stamp-01",
            "rollback to 2026.05.06.4 using Checkout-Release rollback stage",
            "low risk canary with direct rollback",
            "Create local SafeFly draft for explicit review",
            true);

        var storeField = typeof(ReleaseManagerService).GetField("_dataStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = (DataStore)storeField.GetValue(service)!;
        return Assert.Single(store.Data.ReleaseEvidencePackets);
    }

    private static ReleaseSafeFlyDraft CreateCompleteDraft(ReleaseManagerService service, Guid chatId, Guid evidencePacketId)
    {
        InvokePrivateString(
            service,
            "CreateSafeFlyDraft",
            chatId,
            evidencePacketId.ToString(),
            null,
            null,
            null,
            "App Deployment",
            "repo AutoCreate skill",
            null,
            null,
            null,
            "30 minutes clean canary bake",
            null,
            "Draft reviewer summary only; do not send.",
            "async review",
            "https://build/9001; https://dashboard/stamp-01");

        var storeField = typeof(ReleaseManagerService).GetField("_dataStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = (DataStore)storeField.GetValue(service)!;
        return Assert.Single(store.Data.ReleaseSafeFlyDrafts);
    }
}
