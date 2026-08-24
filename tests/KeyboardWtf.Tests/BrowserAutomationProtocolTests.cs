using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationProtocolTests
{
    [Theory]
    [InlineData("No tab with id: 42.")]
    [InlineData("The requested tab is not owned by this browser task.")]
    public void Missing_task_tab_failures_get_one_bounded_recovery(string error)
    {
        Assert.True(BrowserAutomationCoordinator.IsMissingTaskTabError(error));
        Assert.False(BrowserAutomationCoordinator.IsMissingTaskTabError("The page control is stale."));
        Assert.True(BrowserAutomationCoordinator.ShouldRecoverMissingTaskTab(
            "navigate",
            error,
            recoveryAlreadyAttempted: false));
        Assert.False(BrowserAutomationCoordinator.ShouldRecoverMissingTaskTab(
            "navigate",
            error,
            recoveryAlreadyAttempted: true));
        Assert.False(BrowserAutomationCoordinator.ShouldRecoverMissingTaskTab(
            "click",
            error,
            recoveryAlreadyAttempted: false));
    }

    [Theory]
    [InlineData("Please sign in to Gmail.", "login_required")]
    [InlineData("Complete the CAPTCHA manually.", "captcha_required")]
    [InlineData("Confirm the destructive delete.", "confirmation_required")]
    [InlineData("Grant browser permission.", "permission_required")]
    [InlineData("Provide the URL of the target service.", "ambiguous_target")]
    [InlineData("Complete this step in Chrome.", "manual_step")]
    public void Intervention_reason_is_structured_without_losing_the_exact_instruction(
        string reason,
        string expectedKind)
    {
        Assert.Equal(expectedKind, BrowserAutomationCoordinator.ClassifyIntervention(reason));
        var speech = BrowserAutomationCoordinator.BuildInterventionSpeech(reason);
        Assert.Contains(reason, speech);
        Assert.Contains("same task", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancel browser automation", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Voice_tool_cancellation_suppresses_only_the_duplicate_lifecycle_announcement()
    {
        Assert.False(BrowserAutomationCoordinator.ShouldAnnounceOutcome(
            BrowserTaskState.Cancelled,
            suppressCancellationAnnouncement: true));
        Assert.True(BrowserAutomationCoordinator.ShouldAnnounceOutcome(
            BrowserTaskState.Cancelled,
            suppressCancellationAnnouncement: false));
        Assert.True(BrowserAutomationCoordinator.ShouldAnnounceOutcome(
            BrowserTaskState.NeedsUserAction,
            suppressCancellationAnnouncement: true));
    }

    [Fact]
    public void Native_message_frames_round_trip_without_truncation()
    {
        var payload = Encoding.UTF8.GetBytes("{\"text\":\"Jarvis 🟠 browser\"}");
        using var stream = new MemoryStream();

        BrowserNativeMessagingHost.WriteFrame(stream, payload);
        stream.Position = 0;

        Assert.Equal(payload, BrowserNativeMessagingHost.ReadFrame(stream));
    }

    [Fact]
    public void Source_extension_versions_match_the_protocol()
    {
        var root = FindRepositoryRoot();
        var extension = Path.Combine(root, "extensions", "keyboard-wtf-browser-helper-v2");
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(extension, "manifest.json")));
        var expected = BrowserHelperProtocol.Version;

        Assert.Equal(BrowserHelperProtocol.PackageVersion, manifest.RootElement.GetProperty("version").GetString());
        Assert.Contains(
            $"const VERSION = \"{expected}\"",
            File.ReadAllText(Path.Combine(extension, "service-worker.js")));
        Assert.Contains(
            $"const VERSION = \"{expected}\"",
            File.ReadAllText(Path.Combine(extension, "content-script.js")));
        var hostPermissions = manifest.RootElement
            .GetProperty("host_permissions")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("<all_urls>", hostPermissions);

        var permissions = manifest.RootElement
            .GetProperty("permissions")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("alarms", permissions);
        Assert.Contains("webNavigation", permissions);
        Assert.True(manifest.RootElement
            .GetProperty("content_scripts")[0]
            .GetProperty("all_frames")
            .GetBoolean());
        Assert.True(manifest.RootElement
            .GetProperty("content_scripts")[0]
            .GetProperty("match_origin_as_fallback")
            .GetBoolean());

        var externalMatches = manifest.RootElement
            .GetProperty("externally_connectable")
            .GetProperty("matches")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(new[]
        {
            "https://keyboardwtf.com/*",
            "https://www.keyboardwtf.com/*",
            "https://keyboard-wtf.vercel.app/*",
        }, externalMatches);
        Assert.DoesNotContain(externalMatches, match => match?.Contains('*') == true && match.StartsWith("*", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_website_status_requires_a_live_native_handshake_and_exposes_no_page_data()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));
        var website = File.ReadAllText(Path.Combine(root, "production", "site", "chrome-helper-status.js"));

        Assert.Contains("chrome.runtime.onMessageExternal.addListener", worker, StringComparison.Ordinal);
        Assert.Contains("nativePort && nativeHandshakeReady && helper.commandChannelReady", worker, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_SITE_ORIGINS", worker, StringComparison.Ordinal);
        Assert.Contains("keyboard_wtf_public_helper_status", website, StringComparison.Ordinal);
        Assert.Contains("result?.connected === true && result?.ready === true", website, StringComparison.Ordinal);
        Assert.DoesNotContain("activeUrl:", worker[worker.IndexOf("chrome.runtime.onMessageExternal", StringComparison.Ordinal)..worker.IndexOf("function connectNative", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain("activeTitle:", worker[worker.IndexOf("chrome.runtime.onMessageExternal", StringComparison.Ordinal)..worker.IndexOf("function connectNative", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void Restricted_inputs_never_expose_values_or_accept_automation()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "content-script.js"));

        Assert.Contains("function isRestrictedInput(element)", script);
        Assert.Contains("\"password\", \"file\"", script);
        Assert.Contains("Passwords and file uploads require direct user action.", script);
        Assert.Contains("if (isRestrictedInput(element)) return \"\";", script);
        Assert.Contains("input:not([type='password']):not([type='file'])", script);
    }

    [Fact]
    public void Optional_receipt_capture_cannot_block_the_live_action()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("if (command.captureBeforeAction)", worker);
        Assert.Contains("screenshotDataUrl = \"\";", worker);
        Assert.Contains("commit_action", worker);
    }

    [Fact]
    public void Native_bridge_recovers_from_a_sleeping_worker_or_stalled_response()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("chrome.alarms.onAlarm.addListener", worker);
        Assert.Contains("ensureBridgeAlarm", worker);
        Assert.Contains("NATIVE_RESPONSE_TIMEOUT_MS", worker);
        Assert.Contains("stalePort?.disconnect", worker);
        Assert.Contains("pollBusy = false", worker);
        Assert.Contains("clearTimeout(pending.timer)", worker);
        Assert.Contains("lastNativeResponseAt", worker);
    }

    [Fact]
    public void Native_bridge_discards_a_port_that_rejects_post_message_synchronously()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("const port = nativePort;", worker);
        Assert.Contains("port.postMessage(message);", worker);
        Assert.Contains("resetNativePort(String(error?.message || error), port);", worker);
        Assert.Contains("resetNativePort(error.message, port);", worker);
        Assert.Contains("if (expectedPort && nativePort !== expectedPort) return;", worker);
        Assert.DoesNotContain("nativePort.postMessage(message);", worker);
    }

    [Fact]
    public void Active_cursor_paste_is_a_verified_helper_contract()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "content-script.js"));

        Assert.Contains("activeEditable", script);
        Assert.Contains("focus_active_cursor", script);
        Assert.Contains("verify_active_text", script);
        Assert.Contains("valueContains", script);
        Assert.Contains("hidden textarea as the active native", script);
        Assert.DoesNotContain("document.execCommand(\"paste\"", script);

        var coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "BrowserAutomationV2",
            "BrowserAutomationCoordinator.cs"));
        var nativeInput = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "BrowserAutomationV2",
            "BrowserTrustedInput.cs"));
        Assert.Contains("SelectPreviousText", coordinator);
        Assert.Contains("CopySelection", coordinator);
        Assert.Contains("editorReceiptMarker", coordinator);
        Assert.Contains("Atomic native editor paste was verified", coordinator);
        Assert.Contains("ClipboardHelper.Restore", coordinator);
        Assert.Contains("SelectPreviousText", nativeInput);
        Assert.Contains("CopySelection", nativeInput);
        Assert.Contains("DeleteSelection", nativeInput);
        Assert.Contains("CollapseSelectionToEnd", nativeInput);
    }

    [Fact]
    public void Highlight_and_action_share_fresh_live_target_geometry()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "content-script.js"));

        Assert.Contains("Responsive pages can reflow between prepare_action and commit_action.", script);
        Assert.Contains("liveRect = target.getBoundingClientRect();", script);
        Assert.Contains("targetBounds = rectangle(liveRect);", script);
        Assert.Contains("drawHighlight(liveRect);", script);
        Assert.Contains("sameIdentity(targetIdentity, semanticIdentity(target))", script);
    }

    [Fact]
    public void Repeated_same_site_navigation_is_bounded_across_cosmetic_goal_wording()
    {
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new BrowserPlannerAction
        {
            Type = "navigate",
            Url = "https://example.test/search?a=1",
        };
        var second = new BrowserPlannerAction
        {
            Type = "navigate",
            Url = "https://example.test/search?b=2",
        };

        Assert.True(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, first, "Navigate to the search results page.", 2, out _));
        Assert.True(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, second, "Open the standard search interface.", 2, out _));
        Assert.False(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, first, "Return to the main search page.", 2, out var count));
        Assert.Equal(3, count);
    }

    [Fact]
    public void Progressed_url_is_not_discarded_by_a_same_site_home_navigation()
    {
        var page = new BrowserPageSnapshot
        {
            Url = "https://example.test/search?origin=A&destination=B",
        };
        var action = new BrowserPlannerAction
        {
            Type = "navigate",
            Url = "https://example.test/search",
        };

        Assert.True(BrowserAutomationCoordinator.TryGetNavigationRegression(
            "Find and compare the cheapest result.",
            page,
            action,
            out var reason));
        Assert.Contains("removes", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(BrowserAutomationCoordinator.TryGetNavigationRegression(
            "Reset and start over.",
            page,
            action,
            out _));
    }

    [Fact]
    public void Context_free_calendar_day_is_rejected_for_an_explicit_date()
    {
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName = "7",
        };
        var control = new BrowserControl
        {
            Id = "day-7",
            Role = "button",
            AccessibleName = "7",
            Text = "7",
            Visible = true,
            Enabled = true,
        };

        Assert.True(BrowserAutomationCoordinator.TryGetAmbiguousExplicitDateTarget(
            "Select September 7, 2026 as the departure date.",
            action,
            control,
            out var reason));
        Assert.Contains("context-free", reason, StringComparison.OrdinalIgnoreCase);

        control.AccessibleName = "September 7, 2026";
        Assert.False(BrowserAutomationCoordinator.TryGetAmbiguousExplicitDateTarget(
            "Select September 7, 2026 as the departure date.",
            action,
            control,
            out _));
    }

    [Fact]
    public void Grounded_control_cannot_silently_switch_to_another_requested_month()
    {
        var request =
            "Find a round-trip option for September 7 through September 14, 2026.";
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName =
                "Done. Search for round trip options departing on December 7, 2026.",
        };

        Assert.True(BrowserAutomationCoordinator.TryGetExplicitConstraintConflict(
            request,
            action,
            null,
            out var reason));
        Assert.Contains("different month", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_scans_and_executes_controls_inside_normal_child_frames()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));
        var script = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "content-script.js"));

        Assert.Contains("chrome.webNavigation.getAllFrames", worker);
        Assert.Contains("mergeFrameSnapshots", worker);
        Assert.Contains("frameForCommand", worker);
        Assert.Contains("frameIds: [frameId]", worker);
        Assert.Contains("scopedControlId", worker);
        Assert.Contains("isEligibleChildFrame", worker);
        Assert.Contains("isInheritedOriginFrame", worker);
        Assert.Contains("__active_cursor__", worker);
        Assert.Contains("scan(message.taskId, Number(message.frameId || 0))", script);
        Assert.Contains("describeControl(id, element, rect, frameId)", script);
        Assert.Contains("targetId: command.controlId || \"\"", script);
        Assert.Contains("contextualAccessibleLabel", script);
        Assert.Contains("referencedLabelText", script);
        Assert.Contains("A stale short label such as a calendar day is unsafe", script);
    }

    [Fact]
    public void Exact_quoted_current_cursor_text_uses_the_verified_direct_paste_path()
    {
        var page = new BrowserPageSnapshot
        {
            ActiveEditable = true,
            ActiveEditorId = "f7:__active_cursor__",
            ActiveEditorRole = "textbox",
            ActiveEditorName = "Document editor",
        };

        Assert.True(BrowserAutomationCoordinator.TryBuildDirectActiveCursorPasteAction(
            "In this document, place the exact text \"safe validation text\" at the current cursor.",
            page,
            out var action));
        Assert.Equal("paste", action.Type);
        Assert.Equal("f7:__active_cursor__", action.TargetId);
        Assert.Equal("safe validation text", action.Text);
    }

    [Theory]
    [InlineData("Write me a poem.")]
    [InlineData("Summarize this document.")]
    [InlineData("Place the text at the current cursor.")]
    public void Direct_cursor_paste_requires_an_active_cursor_and_explicit_quoted_text(
        string request)
    {
        Assert.False(BrowserAutomationCoordinator.TryBuildDirectActiveCursorPasteAction(
            request,
            new BrowserPageSnapshot
            {
                ActiveEditable = true,
                ActiveEditorId = "f7:__active_cursor__",
            },
            out _));
    }

    [Fact]
    public void Task_tab_activation_requires_an_exact_owned_tab_identifier()
    {
        var owned = new HashSet<int> { 41, 73 };

        Assert.True(BrowserAutomationCoordinator.TryResolveTaskTabTarget(
            new BrowserPlannerAction { Type = "activate_tab", TargetId = "tab:73" },
            owned,
            out var tabId));
        Assert.Equal(73, tabId);
        Assert.False(BrowserAutomationCoordinator.TryResolveTaskTabTarget(
            new BrowserPlannerAction { Type = "activate_tab", TargetId = "tab:99" },
            owned,
            out _));
        Assert.False(BrowserAutomationCoordinator.TryResolveTaskTabTarget(
            new BrowserPlannerAction { Type = "activate_tab", TargetId = "73" },
            owned,
            out _));
    }

    [Theory]
    [InlineData("Summarize this page and paste it into Google Docs.", true)]
    [InlineData("Place the exact text at the current cursor.", true)]
    [InlineData("Summarize this page and copy it to the clipboard.", false)]
    public void Destination_editor_workflows_are_not_terminated_at_intermediate_clipboard_steps(
        string request,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.RequiresDestinationEditorTransfer(request));
    }

    [Fact]
    public void Planner_type_on_the_active_document_editor_uses_trusted_native_paste()
    {
        var page = new BrowserPageSnapshot
        {
            ActiveEditable = true,
            ActiveEditorId = "f9:editor",
            ActiveEditorName = "Document content",
            Controls = new[]
            {
                new BrowserControl
                {
                    Id = "f9:editor",
                    Role = "textbox",
                    AccessibleName = "Document content",
                    Editable = true,
                    Visible = true,
                },
            },
        };

        Assert.True(BrowserAutomationCoordinator.ShouldUseTrustedEditorPaste(
            "Paste the summary into this Google Doc.",
            page,
            new BrowserPlannerAction
            {
                Type = "type",
                TargetId = "f9:editor",
                TargetRole = "textbox",
                TargetName = "Document content",
                Text = "summary",
            }));
        Assert.False(BrowserAutomationCoordinator.ShouldUseTrustedEditorPaste(
            "Fill this form.",
            page,
            new BrowserPlannerAction
            {
                Type = "type",
                TargetId = "f9:editor",
                Text = "answer",
            }));
    }

    [Fact]
    public void Browser_result_text_is_not_persisted_by_general_notifications()
    {
        var root = FindRepositoryRoot();
        var coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "BrowserAutomationV2",
            "BrowserAutomationCoordinator.cs"));
        var notifications = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "NotificationService.cs"));

        Assert.Contains("InfoTransient(\"Browser automation\", finalSummary)", coordinator);
        Assert.Contains("WarningTransient(\"Browser automation\", message)", coordinator);
        Assert.Contains("Show(title, message, ToolTipIcon.Info, writeLog: false)", notifications);
        Assert.Contains("Show(title, message, ToolTipIcon.Warning, writeLog: false)", notifications);
    }

    [Fact]
    public void Unowned_tabs_are_reused_only_for_the_exact_requested_page()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("sameReusablePage(safeUrl(tab.url), target)", worker);
        Assert.Contains("current.pathname", worker);
        Assert.Contains("current.search === target.search", worker);
    }

    [Fact]
    public void Helper_is_installed_into_one_stable_versioned_folder()
    {
        var root = FindRepositoryRoot();
        var source = Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2");
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-helper-{Guid.NewGuid():N}");

        try
        {
            BrowserNativeHostInstaller.InstallExtension(source, destination);

            Assert.True(BrowserNativeHostInstaller.ExtensionVersionMatches(destination));
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(destination, "manifest.json")));
            Assert.Equal(
                BrowserHelperProtocol.PackageVersion,
                manifest.RootElement.GetProperty("version").GetString());
            Assert.Contains(
                $"const VERSION = \"{BrowserHelperProtocol.Version}\"",
                File.ReadAllText(Path.Combine(destination, "service-worker.js")));
            Assert.Contains(
                $"const VERSION = \"{BrowserHelperProtocol.Version}\"",
                File.ReadAllText(Path.Combine(destination, "content-script.js")));
            Assert.True(File.Exists(Path.Combine(destination, "popup.html")));
            Assert.True(File.Exists(Path.Combine(destination, "popup.css")));
            Assert.True(File.Exists(Path.Combine(destination, "popup.js")));
            Assert.True(File.Exists(Path.Combine(destination, "icons", "icon-128.png")));
        }
        finally
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void Native_helper_accepts_the_store_and_development_id_only()
    {
        Assert.Equal(
            "gojdebdikepkpapmnggeljneamjdigbh",
            BrowserHelperProtocol.StoreExtensionId);
        Assert.Equal(
            "gfndpohemndahmmeealkdobepjacblnf",
            BrowserHelperProtocol.DevelopmentExtensionId);
        Assert.True(BrowserHelperProtocol.IsAllowedExtensionId(
            BrowserHelperProtocol.StoreExtensionId));
        Assert.True(BrowserHelperProtocol.IsAllowedExtensionId(
            BrowserHelperProtocol.DevelopmentExtensionId));
        Assert.False(BrowserHelperProtocol.IsAllowedExtensionId(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.True(BrowserNativeMessagingHost.IsNativeHostInvocation(new[]
        {
            $"chrome-extension://{BrowserHelperProtocol.StoreExtensionId}/",
        }));
        Assert.True(BrowserNativeMessagingHost.IsNativeHostInvocation(new[]
        {
            $"chrome-extension://{BrowserHelperProtocol.DevelopmentExtensionId}/",
        }));
        Assert.False(BrowserNativeMessagingHost.IsNativeHostInvocation(new[]
        {
            "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
        }));
    }

    [Fact]
    public void Store_and_development_origins_are_both_registered()
    {
        Assert.Equal(new[]
        {
            $"chrome-extension://{BrowserHelperProtocol.StoreExtensionId}/",
            $"chrome-extension://{BrowserHelperProtocol.DevelopmentExtensionId}/",
        }, BrowserHelperProtocol.AllowedExtensionOrigins);
        Assert.True(BrowserNativeHostInstaller.HasExactAllowedOrigins(
            BrowserHelperProtocol.AllowedExtensionOrigins));
        Assert.False(BrowserNativeHostInstaller.HasExactAllowedOrigins(
            BrowserHelperProtocol.AllowedExtensionOrigins.Append(
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")));
    }

    [Fact]
    public void Native_host_diagnostics_distinguish_current_stale_and_unsafe_registration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-native-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var current = Path.Combine(directory, "current.exe");
            var old = Path.Combine(directory, "old.exe");
            File.WriteAllBytes(current, [0]);
            File.WriteAllBytes(old, [0]);
            var manifest = Path.Combine(directory, "host.json");

            WriteNativeHostManifest(manifest, current, BrowserHelperProtocol.AllowedExtensionOrigins);
            Assert.True(BrowserNativeHostInstaller.EvaluateManifest(manifest, current).Ready);

            WriteNativeHostManifest(manifest, old, BrowserHelperProtocol.AllowedExtensionOrigins);
            var stale = BrowserNativeHostInstaller.EvaluateManifest(manifest, current);
            Assert.False(stale.Ready);
            Assert.Contains("another keyboard.wtf installation", stale.Issue, StringComparison.OrdinalIgnoreCase);

            WriteNativeHostManifest(
                manifest,
                current,
                BrowserHelperProtocol.AllowedExtensionOrigins.Append(
                    "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"));
            var unsafeRegistration = BrowserNativeHostInstaller.EvaluateManifest(manifest, current);
            Assert.False(unsafeRegistration.Ready);
            Assert.Contains("unsafe", unsafeRegistration.Issue, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteNativeHostManifest(
        string path,
        string executable,
        IEnumerable<string> origins) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            name = "wtf.keyboard.browser",
            description = "test",
            path = executable,
            type = "stdio",
            allowed_origins = origins,
        }));

    [Fact]
    public void Two_fresh_helper_identities_fail_closed()
    {
        var now = DateTime.UtcNow;
        var both = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [BrowserHelperProtocol.StoreExtensionId] = now,
            [BrowserHelperProtocol.DevelopmentExtensionId] = now,
        };

        Assert.True(BrowserHelperBridge.HasMultipleFreshExtensionIds(both, now));
        both[BrowserHelperProtocol.DevelopmentExtensionId] = now.AddSeconds(-5);
        Assert.False(BrowserHelperBridge.HasMultipleFreshExtensionIds(both, now));
    }

    [Fact]
    public void Provider_url_keeps_navigation_context_but_redacts_credentials()
    {
        var url =
            "https://example.test/search?q=keyboard&access_token=secret&api_key=private#session=hidden";
        var redacted = BrowserAutomationText.RedactUrlForProvider(url);

        Assert.Contains("q=keyboard", redacted);
        Assert.Contains("access_token=%5Bredacted%5D", redacted);
        Assert.Contains("api_key=%5Bredacted%5D", redacted);
        Assert.Contains("session=%5Bredacted%5D", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("private", redacted);
        Assert.DoesNotContain("hidden", redacted);
    }

    [Fact]
    public void Unicode_truncation_does_not_split_surrogate_pairs()
    {
        var value = "ab🟠cd";
        var truncated = BrowserAutomationText.Truncate(value, 3);

        Assert.Equal("ab🟠", truncated);
        Assert.False(char.IsHighSurrogate(truncated[^1]));
    }

    [Fact]
    public void Protected_active_tab_reports_the_real_readiness_error()
    {
        var error = BrowserHelperBridge.ResolveStatusError(
            BrowserHelperProtocol.Version,
            "",
            "",
            "Open a normal http/https page to use current-tab automation.");

        Assert.Equal(
            "Open a normal http/https page to use current-tab automation.",
            error);
    }

    [Theory]
    [InlineData("chrome://newtab", true)]
    [InlineData("edge://newtab", true)]
    [InlineData("about:blank", true)]
    [InlineData("", true)]
    [InlineData("https://example.test/", false)]
    [InlineData("http://localhost:8090/", false)]
    public void Protected_or_blank_tabs_are_identified_without_affecting_normal_pages(
        string url,
        bool expected) =>
        Assert.Equal(expected, BrowserAutomationCoordinator.IsProtectedOrBlankUrl(url));

    [Theory]
    [InlineData("chrome://newtab", true)]
    [InlineData("chrome://newtab/", true)]
    [InlineData("chrome://new-tab-page", true)]
    [InlineData("edge://newtab/", true)]
    [InlineData("about:blank", true)]
    [InlineData("chrome://extensions", false)]
    [InlineData("chrome://settings", false)]
    [InlineData("https://example.test/", false)]
    [InlineData("", false)]
    public void Only_disposable_protected_tabs_are_eligible_for_service_worker_bootstrap(
        string url,
        bool expected) =>
        Assert.Equal(expected, BrowserHelperBridge.IsDisposableProtectedOrBlankUrl(url));

    [Fact]
    public void Real_start_gate_allows_only_a_fresh_supported_helper_on_a_disposable_tab()
    {
        static BrowserHelperStatus Status(
            bool connected = true,
            bool disposable = true,
            string extensionId = BrowserHelperProtocol.StoreExtensionId,
            string serviceWorkerVersion = BrowserHelperProtocol.Version,
            bool multipleHelpers = false,
            int? windowId = 4,
            int? tabId = 12) => new()
        {
            Connected = connected,
            ActiveTabIsDisposableProtectedOrBlank = disposable,
            ExtensionId = extensionId,
            ServiceWorkerVersion = serviceWorkerVersion,
            MultipleHelpersDetected = multipleHelpers,
            WindowId = windowId,
            TabId = tabId,
        };

        Assert.True(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(Status()));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(connected: false)));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(disposable: false)));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(extensionId: "forged-extension-id")));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(serviceWorkerVersion: "0.0.0")));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(multipleHelpers: true)));
        Assert.False(BrowserHelperBridge.CanBootstrapDisposableProtectedTab(
            Status(tabId: null)));
    }

    [Theory]
    [InlineData("begin_task", true)]
    [InlineData("navigate", true)]
    [InlineData("open_tab", true)]
    [InlineData("click", false)]
    [InlineData("type", false)]
    public void Only_service_worker_safe_commands_bypass_content_script_readiness(
        string action,
        bool expected) =>
        Assert.Equal(expected, BrowserHelperBridge.IsServiceWorkerCommand(action));

    [Fact]
    public void Upcoming_helper_leases_a_protected_tab_then_requires_safe_https_navigation()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("return protectedPageSnapshot(command.taskId, current)", worker,
            StringComparison.Ordinal);
        Assert.Contains("Choose navigate with a safe absolute http/https destination", worker,
            StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.tabs.update(current.id, { url: \"https://", worker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_blank_tab_is_reused_but_an_unrelated_normal_page_is_preserved()
    {
        Assert.False(BrowserAutomationCoordinator.ShouldOpenDestinationInNewTab(
            currentPageIntent: false,
            destinationTabOpened: false,
            initialTabWasProtectedOrBlank: true,
            initialTabId: 7,
            currentTabId: 7,
            actionType: "navigate",
            currentUrl: "chrome://newtab",
            destinationUrl: "https://www.youtube.com/"));

        Assert.True(BrowserAutomationCoordinator.ShouldOpenDestinationInNewTab(
            currentPageIntent: false,
            destinationTabOpened: false,
            initialTabWasProtectedOrBlank: false,
            initialTabId: 7,
            currentTabId: 7,
            actionType: "navigate",
            currentUrl: "https://devpost.com/",
            destinationUrl: "https://www.youtube.com/"));
    }

    [Fact]
    public void Only_the_first_successful_navigation_from_a_protected_tab_gets_a_readiness_wait()
    {
        var navigated = new BrowserActionReceipt { Ok = true, Navigation = true };

        Assert.True(BrowserAutomationCoordinator.ShouldStabilizeProtectedBootstrapNavigation(
            initialTabWasProtectedOrBlank: true,
            alreadyStabilized: false,
            actionType: "navigate",
            navigated));
        Assert.False(BrowserAutomationCoordinator.ShouldStabilizeProtectedBootstrapNavigation(
            initialTabWasProtectedOrBlank: false,
            alreadyStabilized: false,
            actionType: "navigate",
            navigated));
        Assert.False(BrowserAutomationCoordinator.ShouldStabilizeProtectedBootstrapNavigation(
            initialTabWasProtectedOrBlank: true,
            alreadyStabilized: true,
            actionType: "navigate",
            navigated));
        Assert.False(BrowserAutomationCoordinator.ShouldStabilizeProtectedBootstrapNavigation(
            initialTabWasProtectedOrBlank: true,
            alreadyStabilized: false,
            actionType: "click",
            navigated));
        Assert.False(BrowserAutomationCoordinator.ShouldStabilizeProtectedBootstrapNavigation(
            initialTabWasProtectedOrBlank: true,
            alreadyStabilized: false,
            actionType: "navigate",
            new BrowserActionReceipt { Ok = false, Navigation = true }));
    }

    [Fact]
    public void Healthy_command_channel_does_not_surface_a_stale_bridge_error()
    {
        var error = BrowserHelperBridge.ResolveStatusError(
            BrowserHelperProtocol.Version,
            BrowserHelperProtocol.Version,
            "https://example.test/",
            "Keyboard.wtf native bridge heartbeat was stale.",
            BrowserHelperProtocol.StoreExtensionId,
            commandChannelReady: true);

        Assert.Empty(error);
    }

    [Fact]
    public void Stale_content_script_is_distinguished_from_service_worker_mismatch()
    {
        Assert.Equal(
            $"Chrome Helper content script version mismatch. Expected {BrowserHelperProtocol.Version}.",
            BrowserHelperBridge.ResolveStatusError(
                BrowserHelperProtocol.Version,
                "0.6.20",
                "https://example.test/",
                ""));

        Assert.Equal(
            $"Chrome Helper service worker version mismatch. Expected {BrowserHelperProtocol.Version}.",
            BrowserHelperBridge.ResolveStatusError(
                "0.6.20",
                BrowserHelperProtocol.Version,
                "https://example.test/",
            ""));
    }

    [Fact]
    public void Planner_does_not_discard_a_final_required_action()
    {
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            Action = new BrowserPlannerAction
            {
                Type = "copy_clipboard",
                Text = "safe fixture summary",
            },
        };

        BrowserAutomationPlanner.ValidateDecision(decision);

        Assert.False(decision.Done);
        Assert.Equal("copy_clipboard", decision.Action.Type);
    }

    [Fact]
    public void Explicit_done_action_is_terminal()
    {
        var decision = new BrowserPlannerDecision
        {
            Action = new BrowserPlannerAction { Type = "done" },
        };

        BrowserAutomationPlanner.ValidateDecision(decision);

        Assert.True(decision.Done);
    }

    [Theory]
    [InlineData("Click Oldest on this page.", true)]
    [InlineData("Please press the Refresh results icon.", true)]
    [InlineData("Choose Smart.", true)]
    [InlineData("Click Oldest and open the first video.", false)]
    [InlineData("Open Amazon and add a product.", false)]
    [InlineData("Fill this form.", false)]
    public void Only_one_explicit_control_action_can_stop_from_local_evidence(
        string request,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.IsExplicitSingleControlActionRequest(request));
    }

    [Fact]
    public void Unrequested_final_action_is_replanned_instead_of_becoming_user_guidance()
    {
        var decision = new BrowserAutomationSafetyDecision
        {
            MayStart = false,
            PauseBeforeSensitiveStep = true,
            Reason =
                "Pressing Enter here could submit or send. The user did not explicitly request the final action.",
        };

        Assert.True(
            BrowserAutomationCoordinator.IsUnrequestedFinalActionRejection(decision));
        Assert.False(
            BrowserAutomationCoordinator.IsUnrequestedFinalActionRejection(
                new BrowserAutomationSafetyDecision
                {
                    MayStart = false,
                    PauseBeforeSensitiveStep = true,
                    Reason = "This password step requires the user.",
                }));
    }

    [Fact]
    public void Grounded_read_only_noop_is_normalized_to_done()
    {
        var decision = new BrowserPlannerDecision
        {
            ResultSummary = "The current page is a safe fixture.",
            Action = new BrowserPlannerAction { Type = "none" },
        };

        BrowserAutomationPlanner.ValidateDecision(decision);

        Assert.True(decision.Done);
        Assert.Equal("done", decision.Action.Type);
    }

    [Fact]
    public void Unknown_planner_action_is_rejected_before_execution()
    {
        var decision = new BrowserPlannerDecision
        {
            Action = new BrowserPlannerAction { Type = "invented_action" },
        };

        Assert.Throws<InvalidDataException>(
            () => BrowserAutomationPlanner.ValidateDecision(decision));
    }

    [Fact]
    public void Fresh_custom_option_state_confirms_the_requested_value()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                new BrowserControl
                {
                    Role = "option",
                    AccessibleName = "Rock",
                    Selected = true,
                },
            },
        };

        Assert.True(BrowserAutomationCoordinator.FreshStateConfirmsOption(
            page,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "option",
                TargetName = "Rock",
            },
            new BrowserActionReceipt { TargetRole = "option", TargetName = "Rock" }));
    }

    [Theory]
    [InlineData("Choose")]
    [InlineData("Select")]
    [InlineData("Select an option")]
    public void Placeholder_option_is_never_completion_evidence(string placeholder)
    {
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                new BrowserControl
                {
                    Role = "option",
                    AccessibleName = placeholder,
                    Selected = true,
                },
            },
        };

        Assert.False(BrowserAutomationCoordinator.FreshStateConfirmsOption(
            page,
            new BrowserPlannerAction { Type = "click", TargetName = placeholder },
            new BrowserActionReceipt { TargetRole = "option", TargetName = placeholder }));
    }

    [Fact]
    public void Noop_custom_listbox_click_uses_trusted_keyboard_fallback()
    {
        Assert.True(BrowserAutomationCoordinator.ShouldUseListboxKeyboardFallback(
            new BrowserPlannerAction { Type = "click", TargetRole = "listbox" },
            new BrowserActionReceipt
            {
                Ok = true,
                TargetRole = "listbox",
                NoVisibleChange = true,
            }));
        Assert.False(BrowserAutomationCoordinator.ShouldUseListboxKeyboardFallback(
            new BrowserPlannerAction { Type = "click", TargetRole = "button" },
            new BrowserActionReceipt
            {
                Ok = true,
                TargetRole = "button",
                NoVisibleChange = true,
            }));
    }

    [Fact]
    public void Non_native_select_is_resolved_to_the_exact_live_option()
    {
        var action = new BrowserPlannerAction
        {
            Type = "select_option",
            Option = "Rock",
        };
        var receipt = new BrowserActionReceipt
        {
            Ok = false,
            Error = "The target is not a native select control.",
        };
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                new BrowserControl
                {
                    Id = "broad",
                    Role = "listbox",
                    AccessibleName = "What are the planets made of? Rock Gas",
                    Enabled = true,
                    Visible = true,
                    Bounds = new BrowserRectangle { Width = 500, Height = 200 },
                },
                new BrowserControl
                {
                    Id = "rock",
                    Role = "option",
                    AccessibleName = "Rock",
                    Enabled = true,
                    Visible = true,
                    Bounds = new BrowserRectangle { Width = 120, Height = 40 },
                },
                new BrowserControl
                {
                    Id = "gas",
                    Role = "option",
                    AccessibleName = "Gas",
                    Enabled = true,
                    Visible = true,
                    Bounds = new BrowserRectangle { Width = 120, Height = 40 },
                },
            },
        };

        Assert.True(BrowserAutomationCoordinator.ShouldResolveCustomOption(action, receipt));
        Assert.Equal(
            "rock",
            BrowserAutomationCoordinator.FindCustomOptionControl(page, action)?.Id);
    }

    [Theory]
    [InlineData("combobox", false, true)]
    [InlineData("listbox", false, true)]
    [InlineData("textbox", true, false)]
    [InlineData("combobox", true, false)]
    public void Text_for_a_noneditable_choice_uses_option_selection(
        string role,
        bool editable,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.ShouldNormalizeChoiceTyping(
                new BrowserPlannerAction
                {
                    Type = "type",
                    Text = "Speech recognition",
                },
                new BrowserControl
                {
                    Role = role,
                    Editable = editable,
                }));
    }

    [Theory]
    [InlineData("July 29 2026", "2026-07-29")]
    [InlineData("July 29 2026.", "2026-07-29")]
    [InlineData("July 29th, 2026", "2026-07-29")]
    [InlineData("July 29, 2026", "2026-07-29")]
    [InlineData("29 Jul 2026", "2026-07-29")]
    [InlineData("2026-07-29", "2026-07-29")]
    public void Natural_date_text_is_normalized_for_native_date_controls(
        string text,
        string expected)
    {
        Assert.True(
            BrowserAutomationCoordinator.TryNormalizeNativeDateTyping(
                new BrowserPlannerAction
                {
                    Type = "type",
                    Text = text,
                },
                new BrowserControl
                {
                    InputType = "date",
                    Editable = true,
                },
                out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Native_date_paste_is_normalized_before_clipboard_execution()
    {
        Assert.True(
            BrowserAutomationCoordinator.TryNormalizeNativeDateTyping(
                new BrowserPlannerAction
                {
                    Type = "paste",
                    Text = "July 29 2026.",
                },
                new BrowserControl
                {
                    InputType = "date",
                    Editable = true,
                },
                out var normalized));
        Assert.Equal("2026-07-29", normalized);
    }

    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("tomorrow")]
    [InlineData("")]
    public void Ambiguous_or_relative_date_text_is_not_normalized_for_native_date_controls(
        string text)
    {
        Assert.False(
            BrowserAutomationCoordinator.TryNormalizeNativeDateTyping(
                new BrowserPlannerAction
                {
                    Type = "type",
                    Text = text,
                },
                new BrowserControl
                {
                    InputType = "date",
                    Editable = true,
                },
                out _));
    }

    [Fact]
    public void Custom_option_resolution_does_not_guess_a_partial_match()
    {
        var option = BrowserAutomationCoordinator.FindCustomOptionControl(
            new BrowserPageSnapshot
            {
                Controls = new[]
                {
                    new BrowserControl
                    {
                        Id = "rock-and-ice",
                        Role = "option",
                        AccessibleName = "Rock and ice",
                        Enabled = true,
                        Visible = true,
                    },
                },
            },
            new BrowserPlannerAction { Type = "select_option", Option = "Rock" });

        Assert.Null(option);
    }

    [Fact]
    public void Custom_option_can_use_the_immediate_goal_when_the_model_omits_option()
    {
        var option = BrowserAutomationCoordinator.FindCustomOptionControl(
            new BrowserPageSnapshot
            {
                Controls = new[]
                {
                    new BrowserControl
                    {
                        Id = "rock",
                        Role = "div",
                        Text = "Rock",
                        Enabled = true,
                        Visible = true,
                        SafeActions = new[] { "focus", "click" },
                        Bounds = new BrowserRectangle { Width = 120, Height = 40 },
                    },
                },
            },
            new BrowserPlannerAction { Type = "select_option" },
            "Select 'Rock' from the open dropdown.");

        Assert.Equal("rock", option?.Id);
    }

    [Theory]
    [InlineData("Select 'Rock' from the open menu.", "", "Rock")]
    [InlineData("", "Choose Rock for the planet composition question.", "Rock")]
    [InlineData("", "Select option Gas from this dropdown.", "Gas")]
    public void Explicit_custom_option_is_recovered_without_guessing(
        string goal,
        string request,
        string expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.ResolveRequestedCustomOption(
                new BrowserPlannerAction(),
                goal,
                request));
    }

    [Fact]
    public void Repeated_identical_grounded_action_is_bounded()
    {
        var attempts = new Dictionary<string, int>();
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "listbox",
            TargetName = "Planet composition",
        };

        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, 3, out var first));
        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, 3, out var second));
        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, 3, out var third));
        Assert.False(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, 3, out var fourth));
        Assert.Equal((1, 2, 3, 4), (first, second, third, fourth));
    }

    [Fact]
    public void No_progress_strategy_is_scoped_to_the_unchanged_page_state()
    {
        var exhausted = new HashSet<string>();
        var failedPage = new BrowserPageSnapshot
        {
            TabId = 41,
            Fingerprint = "page-state-a",
        };
        var changedPage = new BrowserPageSnapshot
        {
            TabId = 41,
            Fingerprint = "page-state-b",
        };
        var failedAction = new BrowserPlannerAction
        {
            Type = "click",
            TargetId = "g1c9",
            TargetRole = "button",
            TargetName = "Filter",
        };

        BrowserAutomationCoordinator.RememberNoProgressStrategy(
            exhausted,
            failedPage,
            failedAction);

        Assert.True(BrowserAutomationCoordinator.IsKnownNoProgressStrategy(
            exhausted,
            failedPage,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetId = "g2c4",
                TargetRole = "button",
                TargetName = "Filter",
            }));
        Assert.False(BrowserAutomationCoordinator.IsKnownNoProgressStrategy(
            exhausted,
            changedPage,
            failedAction));
        Assert.False(BrowserAutomationCoordinator.IsKnownNoProgressStrategy(
            exhausted,
            failedPage,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "link",
                TargetName = "Requested creator",
            }));
    }

    [Fact]
    public void Repetition_budget_is_scoped_to_page_state()
    {
        var attempts = new Dictionary<string, int>();
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName = "Next",
        };

        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "page-a", 1, out _));
        Assert.False(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "page-a", 1, out _));
        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "page-b", 1, out _));
    }

    [Fact]
    public void Grounded_action_identity_uses_the_live_semantic_name_before_retry_accounting()
    {
        var page = new BrowserPageSnapshot
        {
            Controls =
            [
                new BrowserControl
                {
                    Id = "calendar-day-volatile-42",
                    Role = "button",
                    AccessibleName = "Monday, September 7, 2026",
                    Visible = true,
                    Enabled = true,
                },
            ],
        };
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetId = "calendar-day-volatile-42",
            TargetRole = "button",
            TargetName = "7",
        };

        var control = BrowserAutomationCoordinator.GroundActionIdentity(page, action);

        Assert.NotNull(control);
        Assert.Equal("Monday, September 7, 2026", action.TargetName);
    }

    [Theory]
    [InlineData("Find round-trip flights.", "Done. Search for one-way flights")]
    [InlineData("Play the oldest video.", "Latest")]
    [InlineData("Sort in descending order.", "Ascending")]
    public void Explicit_constraints_reject_semantically_opposite_live_controls(
        string request,
        string targetName)
    {
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName = targetName,
        };

        Assert.True(
            BrowserAutomationCoordinator.TryGetExplicitConstraintConflict(
                request,
                action,
                null,
                out var reason));
        Assert.Contains("contradicts", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strong_verified_checkpoints_are_kept_in_every_planner_window()
    {
        var history = Enumerable.Range(1, 20).Select(index => $"history-{index}").ToArray();
        var checkpoints = new[]
        {
            "range-start: departure date entered and verified",
            "range-end: return date entered and verified",
        };

        var plannerHistory = BrowserAutomationCoordinator.BuildPlannerHistory(
            history,
            checkpoints);

        Assert.Equal(8, plannerHistory.Count);
        Assert.Contains("history-20", plannerHistory);
        Assert.Contains(
            plannerHistory,
            item => item.Contains("range-start", StringComparison.Ordinal)
                && item.Contains("range-end", StringComparison.Ordinal));
    }

    [Fact]
    public void Repetition_budget_allows_a_distinct_semantic_phase_on_the_same_live_control()
    {
        var attempts = new Dictionary<string, int>();
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName = "1",
        };

        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "calendar-state", "Set the departure date.", 1, out _));
        Assert.False(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "calendar-state", "Set the departure date.", 1, out _));
        Assert.True(BrowserAutomationCoordinator.TryRegisterActionAttempt(
            attempts, action, "calendar-state", "Set the return date.", 1, out _));
    }

    [Fact]
    public void Semantic_repetition_budget_survives_rerenders_but_keeps_range_phases_distinct()
    {
        var attempts = new Dictionary<string, int>();
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetRole = "button",
            TargetName = "7",
        };

        Assert.True(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, action, "Set the departure date.", 2, out _));
        Assert.True(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "button",
                TargetName = "7",
                Text = "irrelevant planner narration must not change click identity",
            },
            "Set the departure date.",
            2,
            out _));
        Assert.False(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, action, "Set the departure date.", 2, out _));
        Assert.True(BrowserAutomationCoordinator.TryRegisterSemanticActionAttempt(
            attempts, action, "Set the return date.", 2, out _));
    }

    [Theory]
    [InlineData("Use the second result.", true)]
    [InlineData("Don't submit yet.", true)]
    [InlineData("I completed the login.", true)]
    [InlineData("Switch to Chrome's fourth tab using Ctrl+4.", false)]
    [InlineData("Open a new tab.", false)]
    [InlineData("What time is it?", false)]
    public void Only_explicit_corrections_are_accepted_as_active_task_guidance(
        string utterance,
        bool expected)
    {
        Assert.Equal(expected, BrowserAutomationCoordinator.IsClearlyTaskRelevantGuidance(utterance));
    }

    [Theory]
    [InlineData("Switch to the last tab.", false)]
    [InlineData("Go to the second last tab.", false)]
    [InlineData("Switch to the second-to-last tab.", false)]
    [InlineData("Use August 1 to August 8.", true)]
    public void Tab_commands_stay_out_of_the_active_page_task(
        string utterance,
        bool expected)
    {
        var accepted = BrowserAutomationCoordinator.IsClearlyTaskRelevantGuidance(utterance)
            || BrowserAutomationCoordinator.IsRelevantManualAnswer(
                "Tell me the required departure and return dates.",
                utterance);

        Assert.Equal(expected, accepted);
    }

    [Theory]
    [InlineData("Use Test User as the customer name and continue.", true)]
    [InlineData("Enter Tanush in the first-name field.", true)]
    [InlineData("Open a new tab.", false)]
    [InlineData("Switch to the second browser tab.", false)]
    public void Missing_field_answers_resume_only_the_paused_task(
        string utterance,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.IsRelevantManualAnswer(
                "Please provide the customer name to fill in the form.",
                utterance));
    }

    [Theory]
    [InlineData("cancel browser automation", true)]
    [InlineData("Please pause the browser task.", true)]
    [InlineData("stop", true)]
    [InlineData("continue", false)]
    [InlineData("Use the second result.", false)]
    public void Browser_stop_controls_are_not_misrouted_as_task_guidance(
        string utterance,
        bool expected)
    {
        Assert.Equal(expected, CommandRegistry.IsExplicitBrowserTaskStopOrPause(utterance));
    }

    [Fact]
    public void Planner_provider_health_is_scoped_to_one_browser_task()
    {
        using var planner = new BrowserAutomationPlanner();
        planner.BeginTask("task-a");
        planner.RecordModelFailure(
            "gemini-3-flash",
            new HttpRequestException("429 quota exhausted"));

        Assert.Equal(1, planner.UnhealthyModelCount);

        planner.BeginTask("task-b");

        Assert.Equal(0, planner.UnhealthyModelCount);
    }

    [Fact]
    public async Task A_missing_live_turn_complete_is_recoverable_for_the_next_request()
    {
        var stale = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.False(await GeminiLiveConversationService.WaitForPriorTurnAsync(
            stale.Task,
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None));
        Assert.True(await GeminiLiveConversationService.WaitForPriorTurnAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public void Lifecycle_event_is_marked_as_local_status_data_not_a_new_command()
    {
        var text = GeminiLiveConversationService.BuildLifecycleEventText(
            new JarvisLifecycleAnnouncement
            {
                Source = "browser_automation",
                State = "Completed",
                Detail = "The requested watch page was verified.",
            });

        Assert.StartsWith("[KEYBOARD.WTF_BACKGROUND_LIFECYCLE_EVENT]", text);
        Assert.Contains("\"source\":\"browser_automation\"", text);
        Assert.Contains("\"state\":\"Completed\"", text);
        Assert.Contains("Do not call any tool", text);
        Assert.Contains("no longer running", text);
    }

    [Fact]
    public void Missing_date_is_requested_instead_of_invented()
    {
        var action = new BrowserPlannerAction
        {
            Type = "type",
            TargetRole = "textbox",
            TargetName = "Departure",
            Text = "2025-08-01",
            Replace = true,
        };
        var control = new BrowserControl
        {
            Id = "departure",
            Role = "textbox",
            AccessibleName = "Departure",
            NearbyLabel = "Departure date",
            Visible = true,
            Enabled = true,
            Editable = true,
        };

        Assert.True(BrowserAutomationCoordinator.TryGetMissingRequiredDate(
            "Find flights from Mumbai to Japan.",
            "Enter the departure date.",
            action,
            control,
            out var reason));
        Assert.Contains("not provided", reason, StringComparison.OrdinalIgnoreCase);

        Assert.False(BrowserAutomationCoordinator.TryGetMissingRequiredDate(
            "Find flights from Mumbai to Japan in September 2026 for one week.",
            "Enter the departure date.",
            action,
            control,
            out _));
    }

    [Theory]
    [InlineData("September 2026")]
    [InlineData("2026-09-07")]
    [InlineData("7/9/2026")]
    [InlineData("next weekend")]
    [InlineData("in 2 weeks")]
    public void Natural_date_constraints_are_recognized(string guidance)
    {
        Assert.True(BrowserAutomationCoordinator.HasUsableDateConstraint(guidance));
        Assert.True(BrowserAutomationCoordinator.IsRelevantManualAnswer(
            "Tell me the required travel date or range.",
            guidance));
    }

    [Fact]
    public void Immediate_field_receipt_is_rejected_when_fresh_page_clears_the_value()
    {
        var action = new BrowserPlannerAction
        {
            Type = "type",
            TargetId = "departure",
            TargetName = "Departure",
            Text = "2026-09-07",
            Replace = true,
        };
        var receipt = new BrowserActionReceipt
        {
            Ok = true,
            Action = "type",
            TargetId = "departure",
            TargetName = "Departure",
            ValueMatches = true,
        };
        var cleared = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                new BrowserControl
                {
                    Id = "departure",
                    Role = "textbox",
                    AccessibleName = "Departure",
                    Value = "",
                    Visible = true,
                    Enabled = true,
                    Editable = true,
                },
            },
        };

        Assert.False(BrowserAutomationCoordinator.FreshStateConfirmsPersistedValue(
            cleared,
            action,
            receipt));

        cleared.Controls[0].Value = "09/07/2026";
        Assert.True(BrowserAutomationCoordinator.FreshStateConfirmsPersistedValue(
            cleared,
            action,
            receipt));
    }

    [Fact]
    public void Planner_prompt_forbids_invented_inputs_and_transient_field_success()
    {
        var prompt = BrowserAutomationPlanner.BuildPrompt(
            "Find flights from Mumbai to Japan.",
            new BrowserPageSnapshot(),
            Array.Empty<string>(),
            Array.Empty<BrowserControl>());

        Assert.Contains("Never invent a required date", prompt);
        Assert.Contains("field write is provisional", prompt);
        Assert.Contains("do not alternate between fields", prompt);
    }

    [Fact]
    public void Helper_keeps_dom_actions_in_the_leased_background_tab()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));

        Assert.Contains("if (requiresForeground(command))", worker);
        Assert.Contains("captureTaskTab", worker);
        Assert.Contains("await chrome.tabs.update(restoreTabId, { active: true })", worker);
        Assert.DoesNotContain(
            "await chrome.windows.update(beforeTab.windowId, { focused: true });\n  await chrome.tabs.update(tabId, { active: true });",
            worker);
    }

    [Fact]
    public void Tab_selection_uses_the_exact_existing_window_without_foreground_keys()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "service-worker.js"));
        var start = worker.IndexOf(
            "async function activateExistingTab(command)",
            StringComparison.Ordinal);
        var end = worker.IndexOf(
            "async function navigate(command)",
            start,
            StringComparison.Ordinal);
        var implementation = worker[start..end];

        Assert.Contains("chrome.tabs.query({ windowId: requestedWindowId })", implementation);
        Assert.Contains("await chrome.tabs.update(target.id, { active: true })", implementation);
        Assert.DoesNotContain("chrome.windows.update", implementation);
        Assert.DoesNotContain("SendKeys", implementation, StringComparison.OrdinalIgnoreCase);

        var bridge = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "BrowserAutomationV2",
            "BrowserHelperBridge.cs"));
        Assert.Contains("IsServiceWorkerCommand(action.Type)", bridge);
        Assert.Contains("actionType is \"activate_existing_tab\"", bridge);
        Assert.Contains("status.Connected", bridge);
        Assert.Contains(
            "Service-worker-only commands such as exact tab activation remain safe",
            worker);
        Assert.Contains("chrome.tabs.onActivated.addListener(() => wakeBridge())", worker);
        Assert.Contains("helper_content_heartbeat", worker);
        Assert.Contains("helper_content_heartbeat", File.ReadAllText(Path.Combine(
            root,
            "extensions",
            "keyboard-wtf-browser-helper-v2",
            "content-script.js")));
    }

    [Fact]
    public void Planner_prompt_requires_an_editable_target_or_verified_active_cursor_for_paste()
    {
        var prompt = BrowserAutomationPlanner.BuildPrompt(
            "Summarize this page and paste it into a document.",
            new BrowserPageSnapshot { ActiveEditable = true, ActiveEditorRole = "textbox" },
            Array.Empty<string>(),
            Array.Empty<BrowserControl>());

        Assert.Contains("explicitly editable", prompt);
        Assert.Contains("active cursor", prompt);
        Assert.Contains("task-scoped draft text", prompt);
        Assert.Contains("activate_tab", prompt);
        Assert.Contains("tab:<id>", prompt);
        Assert.Contains("terminal only when the user's requested destination is the clipboard", prompt);
        Assert.Contains("preserved checkpoint", prompt);
        Assert.Contains("never restart the workflow", prompt);
        Assert.Contains("A retry must change strategy", prompt);
    }

    [Fact]
    public void Broad_noneditable_containers_are_not_used_as_editor_targets()
    {
        var action = new BrowserPlannerAction { Type = "click", TargetName = "document area" };
        var container = new BrowserControl
        {
            Role = "div",
            AccessibleName = "Document area",
            Visible = true,
            Enabled = true,
            Editable = false,
            Bounds = new BrowserRectangle { Width = 900, Height = 600 },
        };
        var realButton = new BrowserControl
        {
            Role = "button",
            AccessibleName = "Open menu",
            Visible = true,
            Enabled = true,
            Bounds = new BrowserRectangle { Width = 120, Height = 36 },
        };

        Assert.True(BrowserAutomationCoordinator.IsBroadNonEditorContainerAction(action, container));
        Assert.False(BrowserAutomationCoordinator.IsBroadNonEditorContainerAction(action, realButton));
    }

    [Fact]
    public void Verified_editor_transfer_stops_without_waiting_for_a_redundant_planner_turn()
    {
        var page = new BrowserPageSnapshot
        {
            ActiveEditable = true,
            ActiveEditorId = "editor",
            Controls = new[]
            {
                new BrowserControl { Id = "editor", Role = "textbox", AccessibleName = "Draft editor", Editable = true },
                new BrowserControl { Id = "search", Role = "searchbox", AccessibleName = "Search", Editable = true },
            },
        };
        var request = "Summarize this page and paste the result into the Draft editor.";

        Assert.True(BrowserAutomationCoordinator.IsVerifiedEditorTransferAction(
            request,
            new BrowserActionReceipt { Ok = true, Action = "type", TargetId = "editor", TargetName = "Draft editor", ValueMatches = true },
            page));
        Assert.False(BrowserAutomationCoordinator.IsVerifiedEditorTransferAction(
            request,
            new BrowserActionReceipt { Ok = true, Action = "type", TargetId = "search", TargetName = "Search", ValueMatches = true },
            page));
    }

    [Theory]
    [InlineData("Summarize this page and copy it to the clipboard.", true)]
    [InlineData("Copy the summary into my clipboard.", true)]
    [InlineData("Place \"preserve the original clipboard\" in this Google Doc.", false)]
    [InlineData("Type \"copy this to clipboard\" at the current cursor.", false)]
    [InlineData("Do not change the clipboard.", false)]
    public void Quoted_payload_text_does_not_change_the_terminal_destination(
        string request,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationVerifier.RequestsClipboardOutput(request));
    }

    [Fact]
    public void Stale_or_undispatched_actions_do_not_poison_strategy_recovery()
    {
        Assert.False(BrowserAutomationCoordinator.CanExhaustStrategy(
            new BrowserActionReceipt
            {
                Ok = false,
                Stale = true,
                BeforeFingerprint = "old-page",
            }));
        Assert.False(BrowserAutomationCoordinator.CanExhaustStrategy(
            new BrowserActionReceipt
            {
                Ok = false,
                Error = "The helper temporarily disconnected.",
            }));
        Assert.True(BrowserAutomationCoordinator.CanExhaustStrategy(
            new BrowserActionReceipt
            {
                Ok = true,
                NoVisibleChange = true,
                BeforeFingerprint = "live-page",
            }));
    }

    [Fact]
    public void Planner_requires_collection_context_before_generic_ordering_controls()
    {
        var prompt = BrowserAutomationPlanner.BuildPrompt(
            "Open the oldest item from the requested creator.",
            new BrowserPageSnapshot(),
            Array.Empty<string>(),
            Array.Empty<BrowserControl>());

        Assert.Contains("correct entity, collection, section, and local context", prompt);
        Assert.Contains("global search Filter/Sort control is not collection ordering", prompt);
        Assert.Contains("exact control and mechanism is exhausted", prompt);
    }

    [Fact]
    public void Enter_is_grounded_to_the_unique_search_control_for_a_search_goal()
    {
        var action = new BrowserPlannerAction
        {
            Type = "press_key",
            Key = "Enter",
        };
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                new BrowserControl
                {
                    Id = "search",
                    Role = "combobox",
                    AccessibleName = "Search",
                    Enabled = true,
                    Visible = true,
                },
                new BrowserControl
                {
                    Id = "message",
                    Role = "textbox",
                    AccessibleName = "Message",
                    Enabled = true,
                    Visible = true,
                },
            },
        };

        Assert.True(BrowserAutomationCoordinator.TryGroundSearchEnter(
            page,
            action,
            "Search for the requested channel"));
        Assert.Equal("search", action.TargetId);
        Assert.Equal("Search", action.TargetName);
    }

    [Fact]
    public void Enter_is_not_regrounded_when_search_controls_are_ambiguous()
    {
        var action = new BrowserPlannerAction
        {
            Type = "press_key",
            Key = "Enter",
        };
        var controls = new[]
        {
            new BrowserControl
            {
                Id = "global",
                Role = "searchbox",
                Enabled = true,
                Visible = true,
            },
            new BrowserControl
            {
                Id = "local",
                Role = "searchbox",
                Enabled = true,
                Visible = true,
            },
        };

        Assert.False(BrowserAutomationCoordinator.TryGroundSearchEnter(
            new BrowserPageSnapshot { Controls = controls },
            action,
            "Search for TED"));
        Assert.Empty(action.TargetId);
    }

    [Fact]
    public void Trusted_keyboard_input_uses_the_native_input_union_size()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, BrowserTrustedInput.NativeInputSize);
    }

    [Fact]
    public void Trusted_keyboard_input_can_read_the_current_windows_thread_id()
    {
        Assert.NotEqual(0u, BrowserTrustedInput.CurrentThreadId);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                && Directory.Exists(Path.Combine(directory.FullName, "extensions")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
