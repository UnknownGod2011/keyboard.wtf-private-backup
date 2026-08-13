using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Endpoints;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class SecurityAndUsageTests
{
    [Fact]
    public void Production_configuration_suppresses_framework_http_client_url_logging()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var level = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty("System.Net.Http.HttpClient")
            .GetString();
        Assert.Equal("Warning", level);
    }

    [Fact]
    public void Account_deletion_requires_a_recent_non_future_authentication()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.True(AccountDeletionService.IsRecentAuthentication(now.AddMinutes(-4), now));
        Assert.False(AccountDeletionService.IsRecentAuthentication(now.AddMinutes(-6), now));
        Assert.False(AccountDeletionService.IsRecentAuthentication(now.AddMinutes(2), now));
        Assert.False(AccountDeletionService.IsRecentAuthentication(null, now));
    }

    [Theory]
    [InlineData("http://127.0.0.1:49152/keyboard-wtf/callback", true)]
    [InlineData("http://localhost:49152/keyboard-wtf/callback", true)]
    [InlineData("https://127.0.0.1:49152/keyboard-wtf/callback", false)]
    [InlineData("http://example.com:49152/keyboard-wtf/callback", false)]
    [InlineData("http://127.0.0.1:80/keyboard-wtf/callback", false)]
    [InlineData("http://127.0.0.1:49152/other", false)]
    public void Desktop_callback_is_limited_to_the_exact_loopback_route(string value, bool expected) =>
        Assert.Equal(expected, Crypto.IsSafeLoopbackCallback(value));

    [Fact]
    public void Pkce_is_deterministic_without_exposing_the_verifier()
    {
        var verifier = string.Concat(Enumerable.Repeat("abcDEF012_-", 5));
        var challenge = Crypto.PkceChallenge(verifier);
        Assert.Equal(challenge, Crypto.PkceChallenge(verifier));
        Assert.NotEqual(verifier, challenge);
        Assert.DoesNotContain("=", challenge, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_verified_Google_Firebase_identities_are_accepted()
    {
        var valid = new Dictionary<string, object>
        {
            ["email"] = "person@example.test",
            ["email_verified"] = true,
            ["firebase"] = new Dictionary<string, object> { ["sign_in_provider"] = "google.com" },
        };
        Assert.True(FirebaseIdentityService.IsAllowedGoogleIdentity(valid));

        valid["email_verified"] = false;
        Assert.False(FirebaseIdentityService.IsAllowedGoogleIdentity(valid));
        valid["email_verified"] = true;
        valid["firebase"] = new Dictionary<string, object> { ["sign_in_provider"] = "password" };
        Assert.False(FirebaseIdentityService.IsAllowedGoogleIdentity(valid));
    }

    [Fact]
    public void Json_backed_Firebase_claims_preserve_verified_Google_identity_checks()
    {
        using var document = JsonDocument.Parse("""
            {
              "email": "person@example.test",
              "email_verified": true,
              "firebase": { "sign_in_provider": "google.com" }
            }
            """);
        var claims = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());

        Assert.True(FirebaseIdentityService.IsAllowedGoogleIdentity(claims));

        using var wrongProvider = JsonDocument.Parse("""{"sign_in_provider":"password"}""");
        claims["firebase"] = wrongProvider.RootElement.Clone();
        Assert.False(FirebaseIdentityService.IsAllowedGoogleIdentity(claims));
    }

    [Fact]
    public void Razorpay_signatures_are_checked_with_the_server_secret()
    {
        const string secret = "test_secret_for_unit_tests_only_123456";
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", secret);
        Environment.SetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET", secret);
        try
        {
            var provider = new RazorpayPaymentProvider(
                new HttpClient(),
                Options.Create(TestOptions()));
            var checkout = Crypto.HmacSha256Hex(secret, "order_123|pay_456");
            var subscription = Crypto.HmacSha256Hex(secret, "pay_456|sub_123");
            var webhook = Crypto.HmacSha256Hex(secret, "{\"event\":\"order.paid\"}");
            Assert.True(provider.VerifyOrderCheckoutSignature("order_123", "pay_456", checkout));
            Assert.True(provider.VerifySubscriptionCheckoutSignature("sub_123", "pay_456", subscription));
            Assert.True(provider.VerifyWebhookSignature("{\"event\":\"order.paid\"}", webhook));
            Assert.False(provider.VerifyOrderCheckoutSignature("order_999", "pay_456", checkout));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
            Environment.SetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET", null);
        }
    }

    [Theory]
    [InlineData(PaidDeploymentEnvironment.Staging, "rzp_test_public", true)]
    [InlineData(PaidDeploymentEnvironment.Development, "rzp_test_public", true)]
    [InlineData(PaidDeploymentEnvironment.Production, "rzp_live_public", true)]
    [InlineData(PaidDeploymentEnvironment.Staging, "rzp_live_public", false)]
    [InlineData(PaidDeploymentEnvironment.Production, "rzp_test_public", false)]
    [InlineData(PaidDeploymentEnvironment.Staging, "unknown_public", false)]
    public void Razorpay_key_environment_must_match_the_server_environment(
        PaidDeploymentEnvironment environment,
        string keyId,
        bool valid)
    {
        var options = environment == PaidDeploymentEnvironment.Production
            ? TestOptions(environment, allowBootstrapCatalog: false, useStagingIdentifiers: false)
            : TestOptions(environment);
        if (valid)
            Assert.True(DeploymentSafety.Validate(options, keyId));
        else
            Assert.Throws<InvalidOperationException>(() => DeploymentSafety.Validate(options, keyId));
    }

    [Fact]
    public void Production_rejects_bootstrap_and_staging_identifiers()
    {
        var options = TestOptions(
            PaidDeploymentEnvironment.Production,
            allowBootstrapCatalog: true,
            useStagingIdentifiers: false);
        Assert.Throws<InvalidOperationException>(() =>
            DeploymentSafety.Validate(options, "rzp_live_public"));
    }

    [Fact]
    public void Production_rejects_bootstrap_model_routing_even_with_a_real_catalog()
    {
        var options = TestOptions(
            PaidDeploymentEnvironment.Production,
            allowBootstrapCatalog: false,
            useStagingIdentifiers: false,
            allowBootstrapModelRouting: true);
        Assert.Throws<InvalidOperationException>(() =>
            DeploymentSafety.Validate(options, "rzp_live_public"));
    }

    [Fact]
    public void Production_rejects_staging_owned_billing_export()
    {
        var options = TestOptions(
            PaidDeploymentEnvironment.Production,
            allowBootstrapCatalog: false,
            useStagingIdentifiers: false,
            providerCostReconciliationEnabled: true,
            billingExportTable: "keyboard-wtf-staging.billing_export.gcp_billing_export");

        Assert.Throws<InvalidOperationException>(() =>
            DeploymentSafety.Validate(options, "rzp_live_public"));
    }

    [Theory]
    [InlineData(null, "secret", "webhook")]
    [InlineData("rzp_live_public", null, "webhook")]
    [InlineData("rzp_live_public", "secret", null)]
    public void Production_rejects_incomplete_payment_secrets(
        string? keyId,
        string? keySecret,
        string? webhookSecret)
    {
        var options = TestOptions(
            PaidDeploymentEnvironment.Production,
            allowBootstrapCatalog: false,
            useStagingIdentifiers: false);
        Assert.Throws<InvalidOperationException>(() =>
            DeploymentSafety.ValidateRequiredSecrets(options, keyId, keySecret, webhookSecret));
        Assert.True(DeploymentSafety.ValidateRequiredSecrets(
            options,
            "rzp_live_public",
            "secret",
            "webhook"));
    }

    [Theory]
    [InlineData("owner@example.test", true)]
    [InlineData("OWNER@example.test", true)]
    [InlineData("another@example.test", false)]
    [InlineData("", false)]
    public void Payment_canary_is_limited_to_the_server_allowlist(string email, bool expected)
    {
        var options = new PaidSystemOptions
        {
            PaymentCanaryEmails = ["owner@example.test"],
        };

        Assert.Equal(expected, CatalogService.IsPaymentCanary(options, email));
    }

    [Theory]
    [InlineData("owner@example.test", true)]
    [InlineData("OWNER@example.test", true)]
    [InlineData("another@example.test", false)]
    [InlineData("", false)]
    public void Ai_canary_is_limited_to_the_server_allowlist(string email, bool expected)
    {
        var options = new PaidSystemOptions
        {
            AiCanaryEmails = ["owner@example.test"],
        };

        Assert.Equal(expected, CatalogService.IsAiCanary(options, email));
    }

    [Theory]
    [InlineData("browser-plan", "browser_planner")]
    [InlineData("browser-vision-recovery", "browser_planner")]
    [InlineData("screen-guidance", "screen_reasoning")]
    [InlineData("desktop-structured", "agent_planner")]
    [InlineData("desktop-reformat", "selected_text")]
    [InlineData("live-audio", "live_audio")]
    [InlineData("unknown", "fast_text")]
    public void Task_capabilities_map_to_server_owned_routes(string taskKind, string expected) =>
        Assert.Equal(expected, ModelRoutingService.ResolveRouteName(taskKind));

    [Fact]
    public void Canary_selection_is_deterministic_and_preserves_a_stable_fallback()
    {
        var pricing = new FakeCatalog().Pricing;
        var route = new ModelRouteSnapshot(
            "fast_text", "v1", "vertex",
            [
                new ModelCandidateSnapshot("stable", "region", pricing with { Model = "stable" }),
                new ModelCandidateSnapshot("canary", "region", pricing with { Model = "canary" }),
            ],
            true, false, 1000, 100, 30, 0, 10_000, "1.0.0", "test");

        var selected = ModelRoutingService.ApplyCanary(route, "uid:request");

        Assert.Equal("canary", selected.Candidates[0].Model);
        Assert.Equal("stable", selected.Candidates[1].Model);
        Assert.Equal(
            selected.Candidates.Select(candidate => candidate.Model),
            ModelRoutingService.ApplyCanary(route, "uid:request")
                .Candidates.Select(candidate => candidate.Model));
    }

    [Fact]
    public void A_new_server_model_cannot_inherit_another_models_price()
    {
        var row = new Dictionary<string, object>
        {
            ["primaryModel"] = "gemini-future",
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelRoutingService.RequireExplicitPricing("primary", row));
        Assert.Contains("explicit pricing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_new_live_model_requires_every_live_modality_price()
    {
        var row = new Dictionary<string, object>
        {
            ["primaryModel"] = "gemini-future-live",
            ["primaryPricingVersion"] = "future-live-v1",
            ["primaryInputUsdMicrosPerMillionTokens"] = 1L,
            ["primaryOutputUsdMicrosPerMillionTokens"] = 1L,
            ["primaryCachedInputUsdMicrosPerMillionTokens"] = 1L,
            ["primaryReasoningUsdMicrosPerMillionTokens"] = 1L,
            ["primaryInputAudioUsdMicrosPerMillionTokens"] = 1L,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelRoutingService.RequireExplicitPricing("primary", row, requireLivePricing: true));

        Assert.Contains("LiveInputText", error.Message, StringComparison.Ordinal);
        Assert.Contains("LiveOutputAudio", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.3.1")]
    [InlineData("2.0.0")]
    public void Compatible_desktop_versions_are_accepted(string supplied) =>
        ClientCompatibility.Require(supplied, "1.3.0");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2.99")]
    public void Missing_or_old_desktop_versions_fail_closed(string? supplied) =>
        Assert.Throws<ClientUpgradeRequiredException>(() =>
            ClientCompatibility.Require(supplied, "1.3.0"));

    [Fact]
    public async Task Outdated_desktop_is_rejected_before_reservation_or_provider_dispatch()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var vertex = new FakeVertex(new VertexGenerateResult(
            "never", new ProviderUsage(1, 1, 0, 0, 0, 0, 2, null)));
        var service = CreateAi(store, vertex);

        await Assert.ThrowsAsync<ClientUpgradeRequiredException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-old-desktop", "hello", null, 128),
            "1.2.9",
            CancellationToken.None));

        Assert.Equal(0, store.ReserveCalls);
        Assert.Equal(0, vertex.Calls);
    }

    [Fact]
    public void Payment_evidence_from_another_environment_is_rejected()
    {
        var purchase = new Dictionary<string, object>
        {
            ["userId"] = "uid-1",
            ["environment"] = "staging",
            ["providerObjectId"] = "order_123",
            ["amountMinor"] = 4_900L,
            ["currency"] = "INR",
            ["productSku"] = "credits_test_100",
        };
        Assert.Equal(
            "purchase_environment_mismatch",
            FirestoreAccountStore.PaymentEvidenceRejection(
                "payment.captured",
                "credits",
                "uid-1",
                purchase,
                "order_123",
                "order_123",
                null,
                "pay_123",
                4_900,
                "INR",
                "credits_test_100",
                "production"));
    }

    [Fact]
    public void Replayed_fulfilled_payment_is_not_rejected_by_the_balance_cap()
    {
        var purchase = new Dictionary<string, object>
        {
            ["userId"] = "uid-1",
            ["environment"] = "staging",
            ["providerObjectId"] = "order_123",
            ["amountMinor"] = 4_900L,
            ["currency"] = "INR",
            ["productSku"] = "credits_test_100",
            ["creditMicros"] = 100_000_000L,
        };
        Assert.Null(FirestoreAccountStore.PaymentEvidenceRejection(
            "payment.captured",
            "credits",
            "uid-1",
            purchase,
            "order_123",
            "order_123",
            null,
            "pay_123",
            4_900,
            "INR",
            "credits_test_100",
            "staging",
            currentAvailableCreditMicros: 950_000_000,
            maxAccountCreditMicros: 1_000_000_000,
            creditAlreadyFulfilled: true));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(500, true)]
    [InlineData(404, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public void Model_fallback_is_bounded_to_definitive_non_billable_failures(
        int statusCode,
        bool expected) =>
        Assert.Equal(expected, AiUsageService.IsSafeProviderFallback(statusCode));

    [Fact]
    public async Task Razorpay_reconciliation_inspection_uses_server_authenticated_read_only_endpoints()
    {
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", "unit_test_secret");
        try
        {
            var handler = new RazorpayInspectionHandler();
            var provider = new RazorpayPaymentProvider(
                new HttpClient(handler),
                Options.Create(TestOptions()));

            var order = await provider.InspectOrderAsync("order_123", CancellationToken.None);
            var subscription = await provider.InspectSubscriptionAsync("sub_123", CancellationToken.None);
            var refunds = await provider.InspectRefundsAsync("pay_123", CancellationToken.None);
            var disputes = await provider.InspectDisputesAsync("pay_123", CancellationToken.None);
            var repeatedDisputes = await provider.InspectDisputesAsync("pay_123", CancellationToken.None);

            Assert.Equal("pur_123", order.PurchaseId);
            Assert.Equal("credits_test", order.ProductSku);
            Assert.Single(order.Payments);
            Assert.True(order.Payments[0].Captured);
            Assert.Equal("pur_123", subscription.PurchaseId);
            Assert.Equal("monthly_test", subscription.ProductSku);
            Assert.Single(subscription.Charges);
            Assert.Equal("sub_123", subscription.Charges[0].SubscriptionId);
            Assert.NotNull(subscription.CurrentCycleStartsAt);
            Assert.NotNull(subscription.CurrentCycleEndsAt);
            Assert.NotNull(subscription.Charges[0].InvoicePaidAt);
            Assert.Equal("pay_123", Assert.Single(refunds).PaymentId);
            var dispute = Assert.Single(disputes);
            Assert.Equal("pay_123", dispute.PaymentId);
            Assert.Equal(0, dispute.AmountDeductedMinor);
            Assert.Equal(disputes, repeatedDisputes);
            Assert.Equal(1, handler.Paths.Count(path => path == "disputes"));
            Assert.All(handler.Paths, path => Assert.DoesNotContain("unit_test_secret", path, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
        }
    }

    [Fact]
    public async Task Razorpay_subscription_inspection_rejects_an_invoice_from_another_subscription()
    {
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", "unit_test_secret");
        try
        {
            var provider = new RazorpayPaymentProvider(
                new HttpClient(new RazorpayWrongSubscriptionInvoiceHandler()),
                Options.Create(TestOptions()));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.InspectSubscriptionAsync("sub_123", CancellationToken.None));

            Assert.Contains("outside the requested subscription", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Razorpay_read_only_inspection_retries_bounded_transient_failures(
        HttpStatusCode transientStatus)
    {
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", "unit_test_secret");
        try
        {
            var handler = new RazorpayTransientInspectionHandler(transientStatus, failuresBeforeSuccess: 2);
            var provider = new RazorpayPaymentProvider(
                new HttpClient(handler),
                Options.Create(TestOptions()));

            var subscription = await provider.InspectSubscriptionAsync("sub_123", CancellationToken.None);

            Assert.Equal("cancelled", subscription.Status);
            Assert.Equal(4, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
        }
    }

    [Fact]
    public async Task Razorpay_read_only_inspection_stops_after_bounded_transient_failures()
    {
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", "unit_test_secret");
        try
        {
            var handler = new RazorpayTransientInspectionHandler(
                HttpStatusCode.TooManyRequests,
                failuresBeforeSuccess: int.MaxValue);
            var provider = new RazorpayPaymentProvider(
                new HttpClient(handler),
                Options.Create(TestOptions()));

            var error = await Assert.ThrowsAsync<RazorpayPaymentProvider.RazorpayProviderHttpException>(() =>
                provider.InspectSubscriptionAsync("sub_123", CancellationToken.None));

            Assert.Equal(429, error.StatusCode);
            Assert.Equal(3, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
        }
    }

    [Fact]
    public async Task Razorpay_refund_inspection_reads_every_bounded_page()
    {
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", "rzp_test_public");
        Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", "unit_test_secret");
        try
        {
            var handler = new RazorpayPagedInspectionHandler();
            var provider = new RazorpayPaymentProvider(
                new HttpClient(handler),
                Options.Create(TestOptions()));

            var refunds = await provider.InspectRefundsAsync("pay_123", CancellationToken.None);

            Assert.Equal(101, refunds.Count);
            Assert.Contains("payments/pay_123/refunds?count=100&skip=100", handler.Paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", null);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", null);
        }
    }

    [Theory]
    [InlineData("active", "subscription.activated")]
    [InlineData("paused", "subscription.paused")]
    [InlineData("cancelled", "subscription.cancelled")]
    [InlineData("halted", "subscription.halted")]
    public void Subscription_reconciliation_maps_provider_state_without_crediting_by_state_alone(
        string status,
        string expected) =>
        Assert.Equal(expected, PaymentReconciliationService.SubscriptionLifecycleEvent(status));

    [Fact]
    public void Maintenance_job_selection_is_explicit_and_lease_expiry_is_deterministic()
    {
        Assert.Equal("payments", MaintenanceRunner.ParseJob(["--maintenance-job=payments"]));
        Assert.Null(MaintenanceRunner.ParseJob(["--urls=http://localhost"]));
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        Assert.True(FirestoreMaintenanceLease.CanAcquire(now, now));
        Assert.False(FirestoreMaintenanceLease.CanAcquire(now.AddSeconds(1), now));
    }

    [Fact]
    public void Partial_refund_reverses_only_the_proportional_credit()
    {
        Assert.Equal(
            250_000,
            FirestoreAccountStore.ReversalCreditMicros("refund.processed", 25, 100, 1_000_000));
        Assert.Equal(
            500_000,
            FirestoreAccountStore.ReversalCreditMicros("dispute.lost", 50, 100, 1_000_000));
        Assert.Equal(
            0,
            FirestoreAccountStore.ReversalCreditMicros("refund.processed", null, 100, 1_000_000));
    }

    [Theory]
    [InlineData("open", 4_900, false)]
    [InlineData("under_review", 4_900, false)]
    [InlineData("won", 0, false)]
    [InlineData("closed", 0, false)]
    [InlineData("lost", 0, false)]
    [InlineData("lost", 4_900, true)]
    public void Only_an_authoritatively_deducted_lost_dispute_reverses_credit(
        string status,
        long amountDeductedMinor,
        bool expected) =>
        Assert.Equal(
            expected,
            PaymentReconciliationService.DisputeRequiresCreditReversal(status, amountDeductedMinor));

    [Fact]
    public void Historical_subscription_charges_are_not_assigned_to_the_current_cycle()
    {
        var cycleStart = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var cycleEnd = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        Assert.True(PaymentReconciliationService.IsChargeInCurrentSubscriptionCycle(
            cycleStart,
            cycleStart,
            cycleEnd));
        Assert.False(PaymentReconciliationService.IsChargeInCurrentSubscriptionCycle(
            cycleStart.AddMonths(-1),
            cycleStart,
            cycleEnd));
        Assert.False(PaymentReconciliationService.IsChargeInCurrentSubscriptionCycle(
            null,
            cycleStart,
            cycleEnd));
    }

    [Fact]
    public void Refunds_never_make_available_credit_negative_and_cannot_over_reverse()
    {
        var first = FirestoreAccountStore.CalculatePaymentBalanceChange(
            availableCreditMicros: 100,
            existingDebtMicros: 0,
            grossCreditMicros: 0,
            requestedReversalMicros: 250,
            previouslyReversedMicros: 0,
            purchasedCreditMicros: 1_000);
        Assert.Equal(-100, first.AvailableDeltaMicros);
        Assert.Equal(150, first.NewDebtMicros);
        Assert.Equal(250, first.ReversalCreditMicros);

        var laterPurchase = FirestoreAccountStore.CalculatePaymentBalanceChange(
            availableCreditMicros: 0,
            existingDebtMicros: first.NewDebtMicros,
            grossCreditMicros: 500,
            requestedReversalMicros: 0,
            previouslyReversedMicros: 250,
            purchasedCreditMicros: 1_000);
        Assert.Equal(350, laterPurchase.AvailableDeltaMicros);
        Assert.Equal(0, laterPurchase.NewDebtMicros);

        var capped = FirestoreAccountStore.CalculatePaymentBalanceChange(
            availableCreditMicros: 1_000,
            existingDebtMicros: 0,
            grossCreditMicros: 0,
            requestedReversalMicros: 900,
            previouslyReversedMicros: 750,
            purchasedCreditMicros: 1_000);
        Assert.Equal(250, capped.ReversalCreditMicros);
    }

    [Theory]
    [InlineData("subscription.activated", "subscription", false)]
    [InlineData("subscription.charged", "subscription", true)]
    [InlineData("payment.captured", "subscription", false)]
    [InlineData("order.paid", "credits", true)]
    [InlineData("payment.captured", "credits", true)]
    [InlineData("subscription.charged", "credits", false)]
    public void Only_authoritative_purchase_events_create_credits(
        string eventType,
        string purchaseKind,
        bool expected) =>
        Assert.Equal(expected, FirestoreAccountStore.IsCreditEvent(eventType, purchaseKind));

    [Fact]
    public void Payment_credit_requires_exact_server_owned_identity_amount_currency_and_sku()
    {
        var purchase = new Dictionary<string, object>
        {
            ["userId"] = "uid-owner",
            ["providerObjectId"] = "order_expected",
            ["providerPaymentId"] = "",
            ["amountMinor"] = 4_900L,
            ["currency"] = "INR",
            ["productSku"] = "credits_test",
        };

        string? Validate(
            string objectId = "order_expected",
            long? amount = 4_900,
            string? currency = "INR",
            string? sku = "credits_test") =>
            FirestoreAccountStore.PaymentEvidenceRejection(
                "order.paid",
                "credits",
                "uid-owner",
                purchase,
                objectId,
                objectId,
                null,
                "pay_expected",
                amount,
                currency,
                sku);

        Assert.Null(Validate());
        Assert.Equal("provider_identity_mismatch", Validate(objectId: "order_other"));
        Assert.Equal("payment_amount_mismatch", Validate(amount: 1));
        Assert.Equal("payment_amount_missing", Validate(amount: null));
        Assert.Equal("payment_currency_mismatch", Validate(currency: "USD"));
        Assert.Equal("payment_currency_missing", Validate(currency: null));
        Assert.Equal("payment_sku_mismatch", Validate(sku: "monthly_test"));
        Assert.Equal("payment_sku_missing", Validate(sku: null));
    }

    [Fact]
    public void Payment_credit_rejects_cross_account_purchase_ownership()
    {
        var purchase = new Dictionary<string, object>
        {
            ["userId"] = "uid-other",
            ["providerObjectId"] = "order_expected",
            ["amountMinor"] = 4_900L,
            ["currency"] = "INR",
            ["productSku"] = "credits_test",
        };

        Assert.Equal(
            "purchase_owner_mismatch",
            FirestoreAccountStore.PaymentEvidenceRejection(
                "order.paid",
                "credits",
                "uid-owner",
                purchase,
                "order_expected",
                "order_expected",
                null,
                "pay_expected",
                4_900,
                "INR",
                "credits_test"));
    }

    [Theory]
    [InlineData("none", "monthly_test", "active", true, "monthly_test")]
    [InlineData("none", "monthly_test", "active", false, "none")]
    [InlineData("monthly_test", "monthly_test", "cancelled", true, "none")]
    [InlineData("monthly_test", "monthly_test", "completed", true, "none")]
    [InlineData("monthly_test", "monthly_test", "expired", true, "none")]
    [InlineData("monthly_test", "monthly_test", "halted", true, "none")]
    [InlineData("monthly_test", "monthly_test", "pending", true, "monthly_test")]
    public void Subscription_lifecycle_updates_plan_without_changing_credit_rules(
        string currentPlan,
        string productSku,
        string status,
        bool fulfilled,
        string expected) =>
        Assert.Equal(
            expected,
            FirestoreAccountStore.PlanAfterSubscriptionStatus(currentPlan, productSku, status, fulfilled));

    [Theory]
    [InlineData("", "sub_old", "cancelled", false, 0, 0, false)]
    [InlineData("", "sub_old", "cancelled", true, 0, 0, false)]
    [InlineData("sub_current", "sub_old", "cancelled", true, 10, 20, false)]
    [InlineData("sub_current", "sub_current", "cancelled", true, 10, 10, true)]
    [InlineData("sub_current", "sub_new", "active", true, 10, 20, true)]
    [InlineData("sub_current", "sub_old", "active", true, 20, 10, false)]
    [InlineData("sub_current", "sub_new", "active", false, 10, 20, false)]
    [InlineData("sub_current", "sub_current", "paused", true, 10, 10, true)]
    public void Only_the_owned_or_newly_fulfilled_subscription_updates_account_state(
        string currentSubscriptionId,
        string incomingSubscriptionId,
        string status,
        bool fulfilled,
        long currentCycleDay,
        long incomingCycleDay,
        bool expected) =>
        Assert.Equal(
            expected,
            FirestoreAccountStore.ShouldApplySubscriptionAccountState(
                currentSubscriptionId,
                incomingSubscriptionId,
                status,
                fulfilled,
                DateTimeOffset.UnixEpoch.AddDays(currentCycleDay),
                incomingCycleDay <= 0 ? null : DateTimeOffset.UnixEpoch.AddDays(incomingCycleDay)));

    [Fact]
    public void Lost_dispute_is_a_reversal_event_and_open_dispute_is_not()
    {
        Assert.Equal(
            500_000,
            FirestoreAccountStore.ReversalCreditMicros("dispute.lost", 50, 100, 1_000_000));
        Assert.Equal(
            0,
            FirestoreAccountStore.ReversalCreditMicros("dispute.open", null, 100, 1_000_000));
    }

    [Fact]
    public void Dispute_webhook_uses_authoritative_deducted_amount_not_the_claimed_amount()
    {
        using var document = JsonDocument.Parse(
            """{"payload":{"dispute":{"entity":{"id":"disp_1","payment_id":"pay_1","status":"lost","amount":4900,"amount_deducted":1200,"currency":"INR"}}}}""");
        var parsed = ApiEndpoints.ParseWebhook(document.RootElement, "payment.dispute.lost");
        Assert.Equal("pay_1", parsed.ProviderObjectId);
        Assert.Equal("disp_1", parsed.ProviderEventObjectId);
        Assert.Equal(1_200, parsed.AmountMinor);
        Assert.Equal("lost", parsed.Status);
    }

    [Fact]
    public void Recurring_fulfillment_is_idempotent_by_provider_charge_not_webhook_event()
    {
        var first = FirestoreAccountStore.RecurringFulfillmentId(
            "subscription.charged",
            "pay_same_charge",
            "event_object_one");
        var replay = FirestoreAccountStore.RecurringFulfillmentId(
            "subscription.charged",
            "pay_same_charge",
            "event_object_two");

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        Assert.Null(FirestoreAccountStore.RecurringFulfillmentId(
            "subscription.activated",
            "pay_same_charge",
            "event_object_one"));
        Assert.Equal(first, FirestoreAccountStore.RecurringFulfillmentId(
            "refund.processed",
            "pay_same_charge",
            "refund_object"));
    }

    [Theory]
    [InlineData("subscription.charged", "pay_1", "pay_1", true, true)]
    [InlineData("subscription.charged", "pay_2", "pay_1", true, false)]
    [InlineData("subscription.charged", "pay_1", "pay_1", false, false)]
    [InlineData("subscription.activated", "pay_1", "pay_1", true, false)]
    public void Pre_migration_recurring_charge_replays_are_still_rejected(
        string eventType,
        string providerPaymentId,
        string recordedPaymentId,
        bool purchaseCredited,
        bool expected) =>
        Assert.Equal(
            expected,
            FirestoreAccountStore.IsPreviouslyFulfilledRecurringCharge(
                eventType,
                providerPaymentId,
                recordedPaymentId,
                purchaseCredited));

    [Theory]
    [InlineData("captured", "failed", "credits", true, "payment.failed", "captured")]
    [InlineData("paid", "authorized", "credits", true, "payment.authorized", "paid")]
    [InlineData("captured", "refunded", "credits", true, "refund.processed", "refunded")]
    [InlineData("pending", "failed", "credits", false, "payment.failed", "failed")]
    [InlineData("active", "halted", "subscription", true, "subscription.halted", "halted")]
    public void Stale_payment_events_do_not_downgrade_fulfilled_one_time_purchases(
        string currentStatus,
        string incomingStatus,
        string purchaseKind,
        bool credited,
        string eventType,
        string expected) =>
        Assert.Equal(
            expected,
            FirestoreAccountStore.StatusAfterWebhook(
                currentStatus,
                incomingStatus,
                purchaseKind,
                credited,
                eventType));

    [Theory]
    [InlineData(null, "verification_pending")]
    [InlineData("creating", "verification_pending")]
    [InlineData("pending", "verification_pending")]
    [InlineData("verification_pending", "verification_pending")]
    [InlineData("paid", "paid")]
    [InlineData("captured", "captured")]
    [InlineData("active", "active")]
    [InlineData("failed", "failed")]
    public void Checkout_confirmation_never_downgrades_authoritative_provider_state(
        string? currentStatus,
        string expected) =>
        Assert.Equal(expected, FirestoreAccountStore.StatusAfterCheckoutSignature(currentStatus));

    [Theory]
    [InlineData(null, false, "cancelled")]
    [InlineData("creating", false, "cancelled")]
    [InlineData("pending", false, "cancelled")]
    [InlineData("verification_pending", false, "cancelled")]
    [InlineData("failed", false, "failed")]
    [InlineData("captured", true, "captured")]
    [InlineData("paid", true, "paid")]
    public void Checkout_cancellation_only_closes_non_terminal_uncredited_purchases(
        string? currentStatus,
        bool credited,
        string expected) =>
        Assert.Equal(expected, FirestoreAccountStore.StatusAfterCheckoutCancellation(currentStatus, credited));

    [Theory]
    [InlineData("pending", false, 25, "expired")]
    [InlineData("verification_pending", false, 25, "expired")]
    [InlineData("pending", false, 23, "pending")]
    [InlineData("pending", true, 25, "pending")]
    [InlineData("failed", false, 25, "failed")]
    public void Abandoned_checkout_state_expires_without_overriding_terminal_or_fulfilled_state(
        string status,
        bool credited,
        int ageHours,
        string expected)
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        Assert.Equal(expected, FirestoreAccountStore.StatusAfterAbandonment(
            status,
            credited,
            now.AddHours(-ageHours),
            now));
    }

    [Fact]
    public void Cached_input_tokens_are_discounted_instead_of_double_charged()
    {
        var service = new CatalogService(null!, Options.Create(TestOptions()), new FakeClock());
        var pricing = new PricingSnapshot(
            "test-pricing",
            "gemini-test",
            100_000,
            200_000,
            25_000,
            200_000,
            8_500,
            10_000,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
        var usage = new ProviderUsage(
            InputTokens: 100,
            OutputTokens: 20,
            CachedInputTokens: 40,
            ReasoningTokens: 10,
            ImageTokens: 0,
            AudioTokens: 0,
            TotalTokens: 130,
            ProviderRequestId: "provider-request",
            ProviderModelVersion: "model-version");

        var (providerCost, charge) = service.CalculateActualCost(pricing, usage);

        Assert.Equal(13, providerCost);
        Assert.Equal(10_000, charge);
    }

    [Fact]
    public void Production_catalog_limits_provider_allowance_to_forty_percent_of_gross_revenue()
    {
        var pricing = new FakeCatalog().Pricing with
        {
            UsdToInrPaise = CatalogService.ConservativeUsdToInrPaise,
            CreditMicrosPerInrPaise = 10_000,
        };
        var safe = new CatalogProduct(
            "credits_60", "credits", "60 credits", 10_000, "INR", 60_000_000, null, true);
        CatalogService.ValidateProductionProduct(safe, pricing);

        var unsafeProduct = safe with { CreditMicros = 60_010_000 };
        var error = Assert.Throws<InvalidOperationException>(() =>
            CatalogService.ValidateProductionProduct(unsafeProduct, pricing));
        Assert.Contains("provider-cost share", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_launch_subscription_passes_the_provider_cost_safety_gate()
    {
        var pricing = new FakeCatalog().Pricing with
        {
            UsdToInrPaise = CatalogService.ConservativeUsdToInrPaise,
            CreditMicrosPerInrPaise = 10_000,
        };
        var launch = new CatalogProduct(
            "monthly_standard_inr_999_v1",
            "subscription",
            "Jarvis Monthly",
            99_900,
            "INR",
            500_000_000,
            "plan_ProductionConfiguredAtLaunch",
            true);

        CatalogService.ValidateProductionProduct(launch, pricing);
    }

    [Fact]
    public void Production_launch_subscription_rejects_an_allowance_above_the_safety_gate()
    {
        var pricing = new FakeCatalog().Pricing with
        {
            UsdToInrPaise = CatalogService.ConservativeUsdToInrPaise,
            CreditMicrosPerInrPaise = 10_000,
        };
        var launch = new CatalogProduct(
            "monthly_standard_inr_999_v1",
            "subscription",
            "Jarvis Monthly",
            99_900,
            "INR",
            599_410_000,
            "plan_ProductionConfiguredAtLaunch",
            true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CatalogService.ValidateProductionProduct(launch, pricing));
        Assert.Contains("provider-cost share", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("keyboard-wtf-prod.billing_export.gcp_billing_export_v1_account", true)]
    [InlineData("keyboard-wtf-prod.billing_export.table-with-dash", false)]
    [InlineData("keyboard-wtf-prod.billing_export.table`; DROP TABLE x", false)]
    [InlineData("billing_export.table", false)]
    [InlineData("", false)]
    public void Billing_export_table_is_strictly_validated(string table, bool expected) =>
        Assert.Equal(expected, BigQueryProviderBillingCostSource.IsValidTable(table));

    [Fact]
    public async Task Billing_export_query_is_parameterized_and_returns_only_the_cost_aggregate()
    {
        var handler = new BigQueryBillingHandler();
        var options = Options.Create(TestOptions(
            providerCostReconciliationEnabled: true,
            billingExportTable: "keyboard-wtf-staging.billing_export.gcp_billing_export_v1_account"));
        var source = new BigQueryProviderBillingCostSource(
            new HttpClient(handler),
            new FakeAccessTokenProvider(),
            options);
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

        var result = await source.ReadAsync(start, start.AddDays(1), CancellationToken.None);

        Assert.Equal(123_456, result.ProviderCostUsdMicros);
        Assert.Equal(start, result.WindowStart);
        Assert.Equal(start.AddDays(1), result.WindowEnd);
        Assert.Equal(start.AddDays(2), result.ExportedThrough);
        Assert.True(handler.SawProjectParameter);
        Assert.True(handler.SawClosedWindowParameters);
        Assert.True(handler.SawBytesBilledLimit);
        Assert.Contains("2026-08-01T00:00:00.000000Z", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-01T00:00:00.0000000Z", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("user", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Billing_export_must_be_observed_after_the_closed_window_before_reconciliation_can_pass()
    {
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        Assert.False(ProviderCostReconciliationService.IsBillingExportReady(
            new ProviderBillingCostSnapshot(start, start.AddDays(1), 0, null)));
        Assert.False(ProviderCostReconciliationService.IsBillingExportReady(
            new ProviderBillingCostSnapshot(start, start.AddDays(1), 0, start.AddHours(23))));
        Assert.True(ProviderCostReconciliationService.IsBillingExportReady(
            new ProviderBillingCostSnapshot(start, start.AddDays(1), 0, start.AddDays(2))));
    }

    [Theory]
    [InlineData(1_000_000, 1_050_000, false)]
    [InlineData(1_000_000, 1_200_000, true)]
    [InlineData(0, 200_000, true)]
    [InlineData(0, 0, false)]
    public void Provider_cost_drift_requires_both_absolute_and_relative_materiality(
        long internalCost,
        long billedCost,
        bool expected) =>
        Assert.Equal(
            expected,
            ProviderCostReconciliationService.IsMaterialVariance(
                internalCost,
                billedCost,
                absoluteThreshold: 100_000,
                basisPointThreshold: 1_000));

    [Fact]
    public void Server_fx_floor_is_conservative_relative_to_the_recorded_RBI_reference() =>
        Assert.True(CatalogService.ConservativeUsdToInrPaise >= 9_572);

    [Fact]
    public void Reservation_holds_the_full_approved_budget_and_rejects_an_unsafe_low_cap()
    {
        var service = new CatalogService(null!, Options.Create(TestOptions()), new FakeClock());
        var pricing = new PricingSnapshot(
            "test-pricing", "gemini-test", 300_000, 2_500_000, 30_000, 2_500_000,
            8_700, 10_000, DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        Assert.Equal(
            25_000_000,
            service.EstimateReservationCreditMicros(pricing, "hello", 128, 25_000_000));
        Assert.Throws<InvalidOperationException>(() =>
            service.EstimateReservationCreditMicros(pricing, new string('x', 50_000), 8192, 1));
    }

    [Fact]
    public void Live_pricing_uses_modality_rates_and_safety_margin_only_for_estimates()
    {
        var service = new CatalogService(null!, Options.Create(TestOptions()), new FakeClock());
        var pricing = new PricingSnapshot(
            "test-pricing", "gemini-test", 300_000, 2_500_000, 30_000, 2_500_000,
            8_700, 10_000, DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
        var exact = new ProviderUsage(
            2_000, 1_000, 0, 0, 0, 2_000, 3_000, "provider-live", InputAudioTokens: 2_000,
            OutputAudioTokens: 1_000);
        var estimated = exact with { Estimated = true };

        var exactCost = service.CalculateLiveActualCost(pricing, exact, 12_500);
        var estimatedCost = service.CalculateLiveActualCost(pricing, estimated, 12_500);

        Assert.Equal(18_000, exactCost.ProviderCostUsdMicros);
        Assert.Equal(exactCost.ProviderCostUsdMicros, estimatedCost.ProviderCostUsdMicros);
        Assert.True(estimatedCost.ChargeCreditMicros > exactCost.ChargeCreditMicros);
    }

    [Fact]
    public void Global_quota_shards_are_deterministic_and_preserve_the_exact_daily_cap()
    {
        const long cap = 1_000_000_019;
        const int shards = 32;
        var first = FirestoreAccountStore.GlobalUsageShardIndex("20260804", "uid-1", "request-1", shards);
        var again = FirestoreAccountStore.GlobalUsageShardIndex("20260804", "uid-1", "request-1", shards);

        Assert.Equal(first, again);
        Assert.InRange(first, 0, shards - 1);
        Assert.Equal(cap, Enumerable.Range(0, shards)
            .Sum(index => FirestoreAccountStore.GlobalUsageShardCap(cap, shards, index)));
    }

    [Fact]
    public void Late_legacy_revision_usage_reduces_sharded_capacity_without_caching()
    {
        const long globalCap = 1_000;
        const int shardCount = 4;
        var marker = new Dictionary<string, object>
        {
            ["globalDailyCapCreditMicros"] = globalCap,
            ["shardCount"] = (long)shardCount,
            ["legacyCommittedCreditMicrosAtCutover"] = 100L,
            ["shardedDailyCapCreditMicros"] = 900L,
        };
        var legacy = new Dictionary<string, object>
        {
            ["reservedCreditMicros"] = 150L,
            ["settledCreditMicros"] = 50L,
        };

        var effective = FirestoreAccountStore.EffectiveShardedDailyCap(
            marker, legacy, globalCap, shardCount);

        Assert.Equal(800L, effective);
        Assert.Equal(800L, Enumerable.Range(0, shardCount)
            .Sum(index => FirestoreAccountStore.GlobalUsageShardCap(effective, shardCount, index)));
    }

    [Fact]
    public void Payment_kill_switch_blocks_checkout_product_resolution()
    {
        var snapshot = new CatalogSnapshot(
            "test",
            [new CatalogProduct("credits", "credits", "Credits", 100, "INR", 1_000_000, null, true)],
            new PricingSnapshot("test", "model", 1, 1, 1, 1, 1, 1, DateTimeOffset.UtcNow),
            false,
            null,
            true,
            "Payments disabled");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CatalogService.EnsurePaymentsEnabled(snapshot));
        Assert.Equal("Payments disabled", error.Message);
    }

    [Fact]
    public async Task Successful_provider_result_is_settled_once()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var usage = new ProviderUsage(100, 50, 0, 0, 0, 0, 150, "provider-1");
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult("ok", usage)));
        var result = await service.GenerateAsync(
            Caller,
            new GenerateRequest("request-1", "hello", null, 128),
            CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(1, store.ReserveCalls);
        Assert.Equal(1, store.ProviderStartedCalls);
        Assert.Equal(1, store.ProviderCompletedCalls);
        Assert.Equal("gemini-test", store.CompletedModel);
        Assert.Equal("test-v1", store.CompletedPricingVersion);
        Assert.Equal("fast_text", store.ReservedActivityType);
        Assert.Equal(1, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.True(store.Calls.IndexOf("provider-completed") < store.Calls.IndexOf("settle"));
    }

    [Fact]
    public async Task Provider_evidence_write_failure_never_settles_or_releases_usage()
    {
        var store = new FakeStore { Available = 1_000_000_000, FailProviderCompletion = true };
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult(
            "ok", new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "provider-evidence"))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-evidence-failure", "hello", null, 128),
            CancellationToken.None));

        Assert.Contains("pending reconciliation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.ProviderCompletedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(1, store.ReconciliationCalls);
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_desktop_login_is_state_bound_expiring_and_exactly_once_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var store = new FirestoreAccountStore(db, new SystemClock(), Options.Create(TestOptions()));
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(
            $"integration-login-{suffix}",
            $"login-{suffix}@example.invalid",
            "Integration Login",
            "test");
        DesktopLoginAttempt? attempt = null;
        DesktopLoginAttempt? expiredAttempt = null;
        DesktopSessionTokens? session = null;
        try
        {
            await store.EnsureAccountAsync(caller, CancellationToken.None);
            var state = Crypto.RandomToken(32);
            var nonce = Crypto.RandomToken(32);
            var verifier = Crypto.RandomToken(48);
            attempt = await store.CreateLoginAttemptAsync(
                Crypto.Sha256(state),
                Crypto.Sha256(nonce),
                Crypto.PkceChallenge(verifier),
                "http://127.0.0.1:49152/keyboard-wtf/callback",
                DateTimeOffset.UtcNow.AddMinutes(2),
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AuthorizeLoginAttemptAsync(
                    attempt.AttemptId,
                    Crypto.Sha256(state + "wrong"),
                    Crypto.Sha256(nonce),
                    caller.UserId,
                    CancellationToken.None));

            var authorized = await store.AuthorizeLoginAttemptAsync(
                attempt.AttemptId,
                Crypto.Sha256(state),
                Crypto.Sha256(nonce),
                caller.UserId,
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AuthorizeLoginAttemptAsync(
                    attempt.AttemptId,
                    Crypto.Sha256(state),
                    Crypto.Sha256(nonce),
                    caller.UserId,
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ExchangeLoginAttemptAsync(
                    attempt.AttemptId,
                    Crypto.Sha256(state),
                    Crypto.Sha256(nonce),
                    Crypto.Sha256(authorized.AuthorizationCode + "wrong"),
                    verifier,
                    "Integration device",
                    $"install-{suffix}",
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromDays(30),
                    CancellationToken.None));

            session = await store.ExchangeLoginAttemptAsync(
                attempt.AttemptId,
                Crypto.Sha256(state),
                Crypto.Sha256(nonce),
                Crypto.Sha256(authorized.AuthorizationCode),
                verifier,
                "Integration device",
                $"install-{suffix}",
                TimeSpan.FromMinutes(10),
                TimeSpan.FromDays(30),
                CancellationToken.None);
            var authenticated = await store.AuthenticateDesktopTokenAsync(
                session.AccessToken,
                CancellationToken.None);
            Assert.Equal(caller.UserId, authenticated?.UserId);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ExchangeLoginAttemptAsync(
                    attempt.AttemptId,
                    Crypto.Sha256(state),
                    Crypto.Sha256(nonce),
                    Crypto.Sha256(authorized.AuthorizationCode),
                    verifier,
                    "Integration device",
                    $"install-{suffix}",
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromDays(30),
                    CancellationToken.None));

            expiredAttempt = await store.CreateLoginAttemptAsync(
                Crypto.Sha256(Crypto.RandomToken(32)),
                Crypto.Sha256(Crypto.RandomToken(32)),
                Crypto.PkceChallenge(Crypto.RandomToken(48)),
                "http://127.0.0.1:49153/keyboard-wtf/callback",
                DateTimeOffset.UtcNow.AddSeconds(-1),
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AuthorizeLoginAttemptAsync(
                    expiredAttempt.AttemptId,
                    expiredAttempt.StateHash,
                    expiredAttempt.DeviceNonceHash,
                    caller.UserId,
                    CancellationToken.None));
        }
        finally
        {
            if (attempt is not null)
                await db.Collection("desktopLoginAttempts").Document(attempt.AttemptId).DeleteAsync();
            if (expiredAttempt is not null)
                await db.Collection("desktopLoginAttempts").Document(expiredAttempt.AttemptId).DeleteAsync();
            if (session is not null)
                await db.Collection("desktopSessions").Document(session.SessionId).DeleteAsync();
            await DeleteIntegrationAccountAsync(db, caller.UserId);
        }
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_multi_account_reservations_are_isolated_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var options = Options.Create(TestOptions());
        var store = new FirestoreAccountStore(db, new SystemClock(), options);
        var suffix = Guid.NewGuid().ToString("N");
        var first = new CallerIdentity($"integration-a-{suffix}", $"a-{suffix}@example.invalid", "Integration A", "test");
        var second = new CallerIdentity($"integration-b-{suffix}", $"b-{suffix}@example.invalid", "Integration B", "test");
        var requestId = $"shared-{suffix}";
        try
        {
            await Task.WhenAll(
                store.EnsureAccountAsync(first, CancellationToken.None),
                store.EnsureAccountAsync(second, CancellationToken.None));
            await Task.WhenAll(
                store.GrantCreditsAsync(first.UserId, 1_000_000, "integration isolation", $"grant-a-{suffix}", "integration-test", CancellationToken.None),
                store.GrantCreditsAsync(second.UserId, 2_000_000, "integration isolation", $"grant-b-{suffix}", "integration-test", CancellationToken.None));

            var reservations = await Task.WhenAll(
                store.ReserveAsync(first, requestId, "gemini-test", "test", 100_000, 500_000, 12, 1, 1_000_000_000, CancellationToken.None, "browser_planner"),
                store.ReserveAsync(second, requestId, "gemini-test", "test", 100_000, 500_000, 12, 1, 1_000_000_000, CancellationToken.None, "selected_text"));
            Assert.All(reservations, result => Assert.True(result.Created));
            Assert.Equal(900_000, reservations[0].Account.AvailableCreditMicros);
            Assert.Equal(1_900_000, reservations[1].Account.AvailableCreditMicros);

            var firstUsage = new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "integration-test-a");
            var secondUsage = new ProviderUsage(40, 10, 0, 0, 0, 0, 50, "integration-test-b");
            await Task.WhenAll(
                store.MarkReservationProviderStartedAsync(first.UserId, requestId, CancellationToken.None),
                store.MarkReservationProviderStartedAsync(second.UserId, requestId, CancellationToken.None));
            await Task.WhenAll(
                store.RecordProviderCompletionAsync(first.UserId, requestId, firstUsage, 10, 123, "verified", CancellationToken.None),
                store.RecordProviderCompletionAsync(second.UserId, requestId, secondUsage, 20, 456, "verified", CancellationToken.None));
            var settlements = await Task.WhenAll(
                store.SettleAsync(first.UserId, requestId, firstUsage, 10, 123, "verified", CancellationToken.None),
                store.SettleAsync(second.UserId, requestId, secondUsage, 20, 456, "verified", CancellationToken.None));
            Assert.All(settlements, result => Assert.True(result.Settled));
            Assert.Equal(999_990, settlements[0].Account.AvailableCreditMicros);
            Assert.Equal(1_999_980, settlements[1].Account.AvailableCreditMicros);
            Assert.Single(settlements[0].Account.RecentUsage);
            Assert.Single(settlements[1].Account.RecentUsage);
            Assert.Equal("browser_planner", settlements[0].Account.RecentUsage[0].ActivityType);
            Assert.Equal("selected_text", settlements[1].Account.RecentUsage[0].ActivityType);
            Assert.Equal(10, settlements[0].Account.RecentUsage[0].ChargedCreditMicros);
            Assert.Equal(20, settlements[1].Account.RecentUsage[0].ChargedCreditMicros);
            var usagePage = await store.GetUsagePageAsync(first.UserId, 1, null, CancellationToken.None);
            Assert.Single(usagePage.Items);
            Assert.Null(usagePage.NextCursor);
        }
        finally
        {
            await DeleteIntegrationAccountAsync(db, first.UserId);
            await DeleteIntegrationAccountAsync(db, second.UserId);
        }
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_concurrent_reservations_cannot_overspend_one_account_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var store = new FirestoreAccountStore(db, new SystemClock(), Options.Create(TestOptions()));
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(
            $"integration-race-{suffix}",
            $"race-{suffix}@example.invalid",
            "Integration Race",
            "test");
        try
        {
            await store.EnsureAccountAsync(caller, CancellationToken.None);
            await store.GrantCreditsAsync(
                caller.UserId,
                1_000_000,
                "integration race",
                $"grant-race-{suffix}",
                "integration-test",
                CancellationToken.None);

            async Task<(string RequestId, ReservationResult? Result, Exception? Error)> TryReserveAsync(string requestId)
            {
                try
                {
                    return (requestId, await store.ReserveAsync(
                        caller,
                        requestId,
                        "gemini-test",
                        "test",
                        750_000,
                        1_000_000,
                        12,
                        2,
                        1_000_000_000,
                        CancellationToken.None), null);
                }
                catch (Exception error)
                {
                    return (requestId, null, error);
                }
            }

            var attempts = await Task.WhenAll(
                TryReserveAsync($"race-a-{suffix}"),
                TryReserveAsync($"race-b-{suffix}"));
            var success = Assert.Single(attempts, attempt => attempt.Result?.Created == true);
            Assert.Single(attempts, attempt => attempt.Error is not null);
            var account = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(250_000, account.AvailableCreditMicros);
            Assert.Equal(750_000, account.ReservedCreditMicros);

            await store.ReleaseReservationAsync(
                caller.UserId,
                success.RequestId,
                "integration_cleanup",
                CancellationToken.None);
            var released = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(1_000_000, released.AvailableCreditMicros);
            Assert.Equal(0, released.ReservedCreditMicros);
        }
        finally
        {
            await DeleteIntegrationAccountAsync(db, caller.UserId);
        }
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_burst_reservations_enforce_account_concurrency_and_preserve_the_balance_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var store = new FirestoreAccountStore(db, new SystemClock(), Options.Create(TestOptions()));
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(
            $"integration-burst-{suffix}",
            $"burst-{suffix}@example.invalid",
            "Integration Burst",
            "test");
        try
        {
            await store.EnsureAccountAsync(caller, CancellationToken.None);
            await store.GrantCreditsAsync(
                caller.UserId,
                1_000_000,
                "integration burst",
                $"grant-burst-{suffix}",
                "integration-test",
                CancellationToken.None);

            async Task<(string RequestId, ReservationResult? Result, Exception? Error)> TryReserveAsync(int ordinal)
            {
                var requestId = $"burst-{ordinal}-{suffix}";
                try
                {
                    var result = await store.ReserveAsync(
                        caller,
                        requestId,
                        "gemini-test",
                        "test",
                        100_000,
                        250_000,
                        100,
                        2,
                        1_000_000_000,
                        CancellationToken.None);
                    return (requestId, result, null);
                }
                catch (Exception exception)
                {
                    return (requestId, null, exception);
                }
            }

            var attempts = await Task.WhenAll(Enumerable.Range(0, 24).Select(TryReserveAsync));
            var accepted = attempts.Where(attempt => attempt.Result?.Created == true).ToArray();
            var rejected = attempts.Where(attempt => attempt.Error is not null).ToArray();
            Assert.Equal(2, accepted.Length);
            Assert.Equal(22, rejected.Length);
            Assert.All(rejected, attempt => Assert.NotNull(attempt.Error));

            var reserved = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(800_000, reserved.AvailableCreditMicros);
            Assert.Equal(200_000, reserved.ReservedCreditMicros);

            await Task.WhenAll(accepted.Select(attempt =>
                store.ReleaseReservationAsync(
                    caller.UserId,
                    attempt.RequestId,
                    "integration_burst_cleanup",
                    CancellationToken.None)));
            var released = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(1_000_000, released.AvailableCreditMicros);
            Assert.Equal(0, released.ReservedCreditMicros);
        }
        finally
        {
            await DeleteIntegrationAccountAsync(db, caller.UserId);
        }
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_payment_events_are_reorder_safe_and_subscription_cycles_are_independent_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var store = new FirestoreAccountStore(db, new SystemClock(), Options.Create(TestOptions()));
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(
            $"integration-pay-{suffix}",
            $"pay-{suffix}@example.invalid",
            "Integration Payment",
            "test");
        var orderId = $"order_{suffix}";
        var creditPurchaseId = $"pur_credit_{suffix}";
        var subscriptionId = $"sub_{suffix}";
        var subscriptionPurchaseId = $"pur_subscription_{suffix}";
        var creditPayment = $"pay_credit_{suffix}";
        var paymentOne = $"pay_one_{suffix}";
        var paymentTwo = $"pay_two_{suffix}";
        try
        {
            var unmatched = await store.ApplyPaymentWebhookAsync(
                $"evt_unmatched_{suffix}",
                "order.paid",
                orderId,
                creditPayment,
                orderId,
                null,
                null,
                creditPayment,
                4_900,
                "paid",
                CancellationToken.None);
            Assert.Equal("unmatched", unmatched.Outcome);

            await store.EnsureAccountAsync(caller, CancellationToken.None);
            Assert.True(await store.BeginPurchaseAsync(
                caller.UserId,
                creditPurchaseId,
                new CatalogProduct("credits_integration", "credits", "Credits", 4_900, "INR", 100_000_000, null, true),
                $"idem_credit_{suffix}",
                CancellationToken.None));
            await store.AttachPurchaseProviderObjectAsync(
                caller.UserId,
                creditPurchaseId,
                orderId,
                "order",
                CancellationToken.None);

            var rejectedAmount = await store.ApplyPaymentWebhookAsync(
                $"evt_bad_amount_{suffix}",
                "order.paid",
                orderId,
                creditPayment,
                orderId,
                null,
                creditPurchaseId,
                creditPayment,
                1,
                "paid",
                CancellationToken.None,
                providerCurrency: "INR",
                providerProductSku: "credits_integration");
            Assert.Equal("payment_amount_mismatch", rejectedAmount.Outcome);
            Assert.Equal(0, (await store.GetAccountAsync(caller.UserId, CancellationToken.None)).AvailableCreditMicros);

            var replay = await store.ApplyPaymentWebhookAsync(
                $"evt_unmatched_{suffix}",
                "order.paid",
                orderId,
                creditPayment,
                orderId,
                null,
                creditPurchaseId,
                creditPayment,
                4_900,
                "paid",
                CancellationToken.None,
                providerCurrency: "INR",
                providerProductSku: "credits_integration");
            Assert.Equal("credited", replay.Outcome);
            await store.ApplyPaymentWebhookAsync(
                $"evt_same_payment_{suffix}",
                "payment.captured",
                orderId,
                creditPayment,
                orderId,
                null,
                creditPurchaseId,
                creditPayment,
                4_900,
                "captured",
                CancellationToken.None,
                providerCurrency: "INR",
                providerProductSku: "credits_integration");
            Assert.Equal(100_000_000, (await store.GetAccountAsync(caller.UserId, CancellationToken.None)).AvailableCreditMicros);

            Assert.True(await store.BeginPurchaseAsync(
                caller.UserId,
                subscriptionPurchaseId,
                new CatalogProduct("monthly_integration", "subscription", "Monthly", 9_900, "INR", 500_000_000, "plan_test", true),
                $"idem_subscription_{suffix}",
                CancellationToken.None));
            await store.AttachPurchaseProviderObjectAsync(
                caller.UserId,
                subscriptionPurchaseId,
                subscriptionId,
                "subscription",
                CancellationToken.None);

            await store.ApplyPaymentWebhookAsync(
                $"evt_activated_{suffix}", "subscription.activated", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, paymentOne, 9_900, "active", CancellationToken.None);
            var activated = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal("none", activated.PlanId);
            Assert.Equal(100_000_000, activated.AvailableCreditMicros);
            var activatedPurchase = await db.Collection("accounts").Document(caller.UserId)
                .Collection("purchases").Document(subscriptionPurchaseId)
                .GetSnapshotAsync();
            Assert.Equal("subscription", activatedPurchase.GetValue<string>("kind"));
            Assert.False(activatedPurchase.GetValue<bool>("credited"));

            var firstCycleEnd = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            var firstCharge = await store.ApplyPaymentWebhookAsync(
                $"evt_charge_one_{suffix}", "subscription.charged", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, paymentOne, 9_900, "active", CancellationToken.None,
                firstCycleEnd, "INR", "monthly_integration");
            Assert.Equal("credited", firstCharge.Outcome);
            var firstCycle = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(600_000_000, firstCycle.AvailableCreditMicros);
            Assert.Equal(500_000_000, firstCycle.SubscriptionOriginalCreditMicros);
            Assert.Equal(500_000_000, firstCycle.SubscriptionAllowanceCreditMicros);
            Assert.Equal(0, firstCycle.SubscriptionReservedCreditMicros);
            Assert.Equal(0, firstCycle.SubscriptionConsumedCreditMicros);
            await store.ApplyPaymentWebhookAsync(
                $"evt_charge_one_replay_{suffix}", "subscription.charged", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, paymentOne, 9_900, "active", CancellationToken.None,
                firstCycleEnd, "INR", "monthly_integration");
            var usageRequest = $"usage_between_cycles_{suffix}";
            await store.ReserveAsync(
                caller,
                usageRequest,
                "gemini-test",
                "test",
                20_000_000,
                25_000_000,
                12,
                1,
                1_000_000_000,
                CancellationToken.None);
            var firstCycleReserved = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(
                firstCycleReserved.SubscriptionOriginalCreditMicros,
                firstCycleReserved.SubscriptionAllowanceCreditMicros
                + firstCycleReserved.SubscriptionReservedCreditMicros);
            await store.MarkReservationProviderStartedAsync(caller.UserId, usageRequest, CancellationToken.None);
            var cycleUsage = new ProviderUsage(10, 10, 0, 0, 0, 0, 20, "provider-cycle-test");
            await store.RecordProviderCompletionAsync(
                caller.UserId,
                usageRequest,
                cycleUsage,
                10_000_000,
                1_000,
                "completed",
                CancellationToken.None);
            await store.SettleAsync(
                caller.UserId,
                usageRequest,
                cycleUsage,
                10_000_000,
                1_000,
                "completed",
                CancellationToken.None);
            var firstCycleAfterUsage = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(10_000_000, firstCycleAfterUsage.CycleUsageCreditMicros);
            Assert.Equal(490_000_000, firstCycleAfterUsage.SubscriptionAllowanceCreditMicros);
            Assert.Equal(10_000_000, firstCycleAfterUsage.SubscriptionConsumedCreditMicros);

            var nextCycleEnd = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
            await store.ApplyPaymentWebhookAsync(
                $"evt_charge_two_{suffix}", "subscription.charged", subscriptionId,
                paymentTwo, null, subscriptionId, subscriptionPurchaseId, paymentTwo, 9_900, "active", CancellationToken.None,
                nextCycleEnd, "INR", "monthly_integration");
            var charged = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal("monthly_integration", charged.PlanId);
            Assert.Equal(1_090_000_000, charged.AvailableCreditMicros);
            Assert.Equal(0, charged.CycleUsageCreditMicros);
            Assert.Equal(nextCycleEnd, charged.CycleEndsAt);

            await store.ApplyPaymentWebhookAsync(
                $"evt_refund_one_{suffix}", "refund.processed", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, $"rfnd_one_{suffix}", 9_900, "processed", CancellationToken.None);
            await store.ApplyPaymentWebhookAsync(
                $"evt_refund_two_{suffix}", "refund.processed", subscriptionId,
                paymentTwo, null, subscriptionId, subscriptionPurchaseId, $"rfnd_two_{suffix}", 9_900, "processed", CancellationToken.None);
            var refunded = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
            Assert.Equal(100_000_000, refunded.AvailableCreditMicros);
            Assert.Equal(100_000_000, refunded.TopUpCreditMicros);
            Assert.Equal(0, refunded.SubscriptionAllowanceCreditMicros);
            var refundedAccount = await db.Collection("accounts").Document(caller.UserId).GetSnapshotAsync();
            Assert.Equal(10_000_000, refundedAccount.GetValue<long>("creditDebtMicros"));
            Assert.Equal("monthly_integration", refunded.PlanId);
            Assert.Equal("active", refunded.SubscriptionStatus);
        }
        finally
        {
            await DeleteIntegrationAccountAsync(db, caller.UserId);
        }
    }

    [Fact]
    public async Task Definite_provider_rejection_releases_the_reservation_and_never_settles()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexRequestRejectedException(500)));
        await Assert.ThrowsAsync<VertexRequestRejectedException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-2", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(1, store.ReserveCalls);
        Assert.Equal(1, store.ProviderStartedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(0, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Ambiguous_provider_rejection_is_held_for_reconciliation()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexRequestRejectedException(429)));
        await Assert.ThrowsAsync<VertexRequestRejectedException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-ambiguous-rejection", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(1, store.ProviderStartedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(1, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Ambiguous_provider_failure_is_held_for_reconciliation()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new HttpRequestException("connection ended")));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-ambiguous", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(1, store.ProviderStartedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(1, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Kill_switch_prevents_a_reservation_or_provider_call()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var catalog = new FakeCatalog { KillSwitch = true };
        var vertex = new FakeVertex(new VertexGenerateResult("never", new ProviderUsage(1, 1, 0, 0, 0, 0, 2, null)));
        var service = CreateAi(store, vertex, catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-3", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(0, store.ReserveCalls);
        Assert.Equal(0, vertex.Calls);
        Assert.Equal(0, store.ProviderStartedCalls);
    }

    [Fact]
    public async Task Ai_canary_can_use_ai_while_the_global_kill_switch_remains_closed()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var catalog = new FakeCatalog { KillSwitch = true };
        var vertex = new FakeVertex(new VertexGenerateResult(
            "ok",
            new ProviderUsage(1, 1, 0, 0, 0, 0, 2, "provider-request", "model-version")));
        var service = CreateAi(store, vertex, catalog, [Caller.Email]);

        var response = await service.GenerateAsync(
            Caller,
            new GenerateRequest("request-ai-canary", "hello", null, 128),
            CancellationToken.None);

        Assert.True(response.Verified);
        Assert.Equal(1, vertex.Calls);
        Assert.Equal(1, store.ReserveCalls);
        Assert.Equal(1, store.SettleCalls);
    }

    [Fact]
    public async Task Cancellation_after_provider_dispatch_is_held_for_reconciliation()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(cancelWhenCalled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-cancelled", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(1, store.ReserveCalls);
        Assert.Equal(1, store.ProviderStartedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(1, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Empty_successful_provider_response_is_accounted_before_failure_is_reported()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult(
            "", new ProviderUsage(10, 0, 0, 0, 0, 0, 10, "provider-request", "model-version"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-empty-output", "hello", null, 128),
            CancellationToken.None));
        Assert.Equal(1, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(0, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Duplicate_request_id_never_invokes_the_provider_again()
    {
        var store = new FakeStore { Available = 1_000_000_000, ReserveCreated = false };
        var vertex = new FakeVertex(new VertexGenerateResult(
            "never", new ProviderUsage(1, 1, 0, 0, 0, 0, 2, null)));
        var service = CreateAi(store, vertex);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-duplicate", "hello", null, 128),
            CancellationToken.None));

        Assert.Equal(0, vertex.Calls);
        Assert.Equal(0, store.ProviderStartedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
    }

    [Fact]
    public async Task Completed_provider_usage_is_settled_even_if_the_client_cancels()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult(
            "ok", new ProviderUsage(10, 5, 0, 0, 0, 0, 15, null))));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await service.GenerateAsync(
            Caller,
            new GenerateRequest("request-provider-complete", "hello", null, 128),
            cancelled.Token);

        Assert.True(result.Verified);
        Assert.Equal(1, store.SettleCalls);
        Assert.False(store.SettlementCancellationRequested);
        Assert.Equal(0, store.ReleaseCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public async Task Invalid_output_budget_is_rejected_before_reservation(int maxOutputTokens)
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult(
            "never", new ProviderUsage(1, 1, 0, 0, 0, 0, 2, null))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest("request-invalid-output", "hello", null, maxOutputTokens),
            CancellationToken.None));
        Assert.Equal(0, store.ReserveCalls);
    }

    [Fact]
    public async Task Invalid_image_is_rejected_before_reservation()
    {
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = CreateAi(store, new FakeVertex(new VertexGenerateResult(
            "never", new ProviderUsage(1, 1, 0, 0, 0, 0, 2, null))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(
            Caller,
            new GenerateRequest(
                "request-invalid-image",
                "describe",
                null,
                128,
                Images: [new InlineImageRequest("image/png", "not-base64")]),
            CancellationToken.None));
        Assert.Equal(0, store.ReserveCalls);
    }

    [Fact]
    public async Task Desktop_login_start_does_not_put_a_reusable_token_in_the_browser_url()
    {
        var store = new FakeStore();
        var service = new DesktopLoginService(store, Options.Create(TestOptions()), new FakeClock());
        var response = await service.StartAsync(new DesktopLoginStartRequest(
            new string('s', 40),
            new string('n', 40),
            new string('c', 43),
            "http://127.0.0.1:49152/keyboard-wtf/callback"), CancellationToken.None);
        Assert.Contains("attempt=", response.BrowserUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("token", response.BrowserUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code=", response.BrowserUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Live_proxy_applies_server_owned_setup_policy()
    {
        const string expectedModel =
            "projects/keyboard-wtf-staging/locations/us-central1/publishers/google/models/gemini-live-test";
        var rewritten = VertexLiveProxyService.RewriteSetup(
            """{"setup":{"model":"models/untrusted","generationConfig":{"responseModalities":["TEXT"],"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Puck"}}}},"tools":[{"functionDeclarations":[{"name":"open_app","description":"Open an app"}]}],"systemInstruction":{"parts":[{"text":"client context"}]},"contextWindowCompression":{"triggerTokens":1}}}""",
            expectedModel);
        using var document = JsonDocument.Parse(rewritten);
        var setup = document.RootElement.GetProperty("setup");
        Assert.Equal(expectedModel, setup.GetProperty("model").GetString());
        Assert.Equal(
            "AUDIO",
            setup.GetProperty("generationConfig")
                .GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Puck", setup.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig")
            .GetProperty("voiceName").GetString());
        Assert.Contains("client context", setup.GetProperty("systemInstruction")
            .GetProperty("parts")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(setup.TryGetProperty("contextWindowCompression", out _));
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
    }

    [Fact]
    public void Live_proxy_rejects_unapproved_tool_exposure() =>
        Assert.Throws<ArgumentException>(() => VertexLiveProxyService.RewriteSetup(
            """{"setup":{"tools":[{"functionDeclarations":[{"name":"steal_credentials"}]}]}}""",
            "projects/test/locations/test/publishers/google/models/test"));

    [Fact]
    public void Live_proxy_rejects_a_non_setup_first_message() =>
        Assert.Throws<ArgumentException>(() => VertexLiveProxyService.RewriteSetup(
            """{"clientContent":{"turnComplete":true}}""",
            "projects/test/locations/test/publishers/google/models/test"));

    [Theory]
    [InlineData(false, 0, "Release")]
    [InlineData(true, 0, "Reconcile")]
    [InlineData(false, 1, "Settle")]
    [InlineData(true, 1, "Settle")]
    public void Live_accounting_never_releases_after_provider_dispatch_without_usage_evidence(
        bool providerDispatchStarted,
        long totalTokens,
        string expectedDisposition)
    {
        Assert.Equal(
            Enum.Parse<LiveAccountingDisposition>(expectedDisposition),
            VertexLiveProxyService.DetermineAccountingDisposition(
                providerDispatchStarted,
                totalTokens));
    }

    [Fact]
    public void Live_usage_uses_conservative_audio_estimates_when_provider_counts_are_absent()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(JsonSerializer.Serialize(new
        {
            realtimeInput = new
            {
                audio = new { data = Convert.ToBase64String(new byte[1_280]) },
            },
        }));
        usage.ObserveProvider(JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                modelTurn = new
                {
                    parts = new[]
                    {
                        new { inlineData = new { data = Convert.ToBase64String(new byte[1_920]) } },
                    },
                },
            },
        }));
        var snapshot = usage.Snapshot();
        Assert.Equal(1, snapshot.InputTokens);
        Assert.Equal(1, snapshot.OutputTokens);
        Assert.Equal(1, snapshot.InputAudioTokens);
        Assert.Equal(1, snapshot.OutputAudioTokens);
        Assert.Equal(2, snapshot.AudioTokens);
        Assert.Equal(2, snapshot.TotalTokens);
        Assert.True(snapshot.Estimated);
    }

    [Fact]
    public void Live_usage_counts_text_when_provider_metadata_is_absent()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient("""{"clientContent":{"turns":[{"parts":[{"text":"12345678"}]}]}}""");
        usage.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"123456789012"}]}}}""");

        var snapshot = usage.Snapshot();

        Assert.Equal(2, snapshot.InputTokens);
        Assert.Equal(3, snapshot.OutputTokens);
        Assert.Equal(5, snapshot.TotalTokens);
        Assert.True(snapshot.Estimated);
    }

    [Fact]
    public void Live_usage_prefers_provider_metadata_and_preserves_provider_identity()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveProvider("""
            {
              "responseId":"response-1",
              "modelVersion":"live-version",
              "usageMetadata":{
                "promptTokenCount":30,
                "candidatesTokenCount":20,
                "thoughtsTokenCount":2,
                "totalTokenCount":52,
                "promptTokensDetails":[{"modality":"AUDIO","tokenCount":25}],
                "candidatesTokensDetails":[{"modality":"AUDIO","tokenCount":15}]
              }
            }
            """);

        var snapshot = usage.Snapshot();

        Assert.Equal(30, snapshot.InputTokens);
        Assert.Equal(20, snapshot.OutputTokens);
        Assert.Equal(25, snapshot.InputAudioTokens);
        Assert.Equal(15, snapshot.OutputAudioTokens);
        Assert.Equal(2, snapshot.ReasoningTokens);
        Assert.Equal("response-1", snapshot.ProviderRequestId);
        Assert.Equal("live-version", snapshot.ProviderModelVersion);
        Assert.False(snapshot.Estimated);
    }

    [Fact]
    public void Live_usage_treats_provider_metadata_as_cumulative_high_water_marks()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":20,"totalTokenCount":50}}""");
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":20,"totalTokenCount":50}}""");
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":7,"totalTokenCount":19}}""");

        var snapshot = usage.Snapshot();

        Assert.Equal(30, snapshot.InputTokens);
        Assert.Equal(20, snapshot.OutputTokens);
        Assert.Equal(50, snapshot.TotalTokens);
    }

    [Theory]
    [MemberData(nameof(LiveCumulativeUsageCases))]
    public void Live_usage_charges_only_the_highest_cumulative_snapshot(
        long[] snapshots,
        long expected)
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        foreach (var value in snapshots)
        {
            usage.ObserveProvider(JsonSerializer.Serialize(new
            {
                usageMetadata = new
                {
                    promptTokenCount = value,
                    candidatesTokenCount = 0,
                    totalTokenCount = value,
                },
            }));
        }

        var result = usage.Snapshot();

        Assert.Equal(expected, result.InputTokens);
        Assert.Equal(expected, result.TotalTokens);
    }

    public static TheoryData<long[], long> LiveCumulativeUsageCases => new()
    {
        { [100, 200, 300], 300 },
        { [100, 200, 200, 200], 200 },
        { [100, 300, 200], 300 },
    };

    [Fact]
    public void Live_idle_begins_only_after_a_completed_turn_and_resets_on_new_activity()
    {
        var activity = new LiveActivityTracker();
        activity.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        Assert.NotNull(activity.LastCompletedTurnAt);

        activity.ObserveProvider("""{"serverContent":{"inputTranscription":{"text":"continue"}}}""");
        Assert.Null(activity.LastCompletedTurnAt);

        activity.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        activity.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"answer"}]}}}""");
        Assert.Null(activity.LastCompletedTurnAt);
    }

    [Theory]
    [InlineData(0, 1_000_000, 5_000_000, 1_000_000)]
    [InlineData(750_000, 1_000_000, 5_000_000, 1_750_000)]
    [InlineData(4_500_000, 1_000_000, 5_000_000, 5_000_000)]
    public void Live_reservations_extend_in_bounded_rolling_windows(
        long charged,
        long window,
        long cap,
        long expected) =>
        Assert.Equal(expected, VertexLiveProxyService.RequiredLiveReservation(charged, window, cap));

    private static readonly CallerIdentity Caller = new(
        "uid-1", "person@example.test", "Test Person", "firebase");

    private static AiUsageService CreateAi(
        FakeStore store,
        FakeVertex vertex,
        FakeCatalog? catalog = null,
        string[]? aiCanaryEmails = null)
    {
        var selectedCatalog = catalog ?? new FakeCatalog();
        return new(
            store,
            selectedCatalog,
            new FakeRouting(selectedCatalog.Pricing),
            vertex,
            Options.Create(TestOptions(aiCanaryEmails: aiCanaryEmails)));
    }

    private static async Task DeleteIntegrationAccountAsync(FirestoreDb db, string userId)
    {
        var account = db.Collection("accounts").Document(userId);
        var reconciliationResourceHashes = new HashSet<string>(StringComparer.Ordinal);
        var globalUsageAdjustments = new Dictionary<string, (long Reserved, long Settled, long ProviderCost, long ProviderSettlements)>(StringComparer.Ordinal);
        foreach (var collectionName in new[] { "ledger", "reservations", "usage", "purchases", "devices", "sessions", "dailyUsage" })
        {
            var documents = await account.Collection(collectionName).Limit(100).GetSnapshotAsync();
            if (documents.Count == 0)
                continue;
            if (collectionName is "reservations" or "purchases")
            {
                foreach (var document in documents.Documents)
                    reconciliationResourceHashes.Add(Crypto.Sha256(document.Reference.Path));
            }
            if (collectionName == "reservations")
            {
                foreach (var document in documents.Documents)
                {
                    var data = document.ToDictionary();
                    var shardId = Convert.ToString(data.GetValueOrDefault("globalUsageShardId")) ?? "";
                    if (string.IsNullOrWhiteSpace(shardId))
                        continue;
                    var status = Convert.ToString(data.GetValueOrDefault("status")) ?? "";
                    var reserved = Convert.ToInt64(data.GetValueOrDefault("reservedCreditMicros") ?? 0L);
                    var charged = Convert.ToInt64(data.GetValueOrDefault("chargedCreditMicros") ?? 0L);
                    var providerCost = Convert.ToInt64(data.GetValueOrDefault("providerCostUsdMicros") ?? 0L);
                    var current = globalUsageAdjustments.GetValueOrDefault(shardId);
                    globalUsageAdjustments[shardId] = string.Equals(status, "settled", StringComparison.Ordinal)
                        ? (current.Reserved, checked(current.Settled + charged),
                            checked(current.ProviderCost + providerCost), checked(current.ProviderSettlements + 1))
                        : status is "released" or "failed"
                            ? current
                            : (checked(current.Reserved + reserved), current.Settled,
                                current.ProviderCost, current.ProviderSettlements);
                }
            }
            var batch = db.StartBatch();
            foreach (var document in documents.Documents)
                batch.Delete(document.Reference);
            await batch.CommitAsync();
        }
        if (globalUsageAdjustments.Count > 0)
        {
            var batch = db.StartBatch();
            foreach (var (shardId, adjustment) in globalUsageAdjustments)
            {
                batch.Set(db.Collection("globalUsageShards").Document(shardId), new Dictionary<string, object>
                {
                    ["reservedCreditMicros"] = FieldValue.Increment(-adjustment.Reserved),
                    ["settledCreditMicros"] = FieldValue.Increment(-adjustment.Settled),
                    ["providerCostUsdMicros"] = FieldValue.Increment(-adjustment.ProviderCost),
                    ["providerCostSettlementCount"] = FieldValue.Increment(-adjustment.ProviderSettlements),
                    ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow),
                }, SetOptions.MergeAll);
            }
            await batch.CommitAsync();
        }

        var idempotency = await db.Collection("idempotency")
            .WhereEqualTo("userId", userId)
            .Limit(100)
            .GetSnapshotAsync();
        if (idempotency.Count > 0)
        {
            var batch = db.StartBatch();
            foreach (var document in idempotency.Documents)
                batch.Delete(document.Reference);
            await batch.CommitAsync();
        }
        foreach (var collectionName in new[] { "paymentEvents", "paymentFulfillments", "auditEvents" })
        {
            var documents = await db.Collection(collectionName)
                .WhereEqualTo("userId", userId)
                .Limit(100)
                .GetSnapshotAsync();
            if (documents.Count == 0)
                continue;
            var batch = db.StartBatch();
            foreach (var document in documents.Documents)
                batch.Delete(document.Reference);
            await batch.CommitAsync();
        }
        foreach (var resourcePathHash in reconciliationResourceHashes)
        {
            var issues = await db.Collection("reconciliationIssues")
                .WhereEqualTo("resourcePathHash", resourcePathHash)
                .Limit(100)
                .GetSnapshotAsync();
            if (issues.Count == 0)
                continue;
            var batch = db.StartBatch();
            foreach (var issue in issues.Documents)
                batch.Delete(issue.Reference);
            await batch.CommitAsync();
        }
        await account.DeleteAsync();
    }

    private static PaidSystemOptions TestOptions(
        PaidDeploymentEnvironment environment = PaidDeploymentEnvironment.Staging,
        bool allowBootstrapCatalog = true,
        bool useStagingIdentifiers = true,
        bool? allowBootstrapModelRouting = null,
        bool providerCostReconciliationEnabled = false,
        string billingExportTable = "",
        string[]? aiCanaryEmails = null) => new()
    {
        DeploymentEnvironment = environment,
        ProjectId = useStagingIdentifiers ? "keyboard-wtf-staging" : "keyboard-wtf-prod",
        VertexModel = "gemini-test",
        WebBaseUrl = useStagingIdentifiers
            ? "https://staging.example.test"
            : "https://keyboard.wtf",
        AllowedOrigins = useStagingIdentifiers
            ? ["https://staging.example.test"]
            : ["https://keyboard.wtf"],
        MaxRequestSeconds = 5,
        DefaultTaskCreditMicrosCap = 25_000_000,
        AllowBootstrapCatalog = allowBootstrapCatalog,
        AllowBootstrapModelRouting = allowBootstrapModelRouting
            ?? environment != PaidDeploymentEnvironment.Production,
        ProviderCostReconciliationEnabled = providerCostReconciliationEnabled,
        BillingExportTable = billingExportTable,
        AiCanaryEmails = aiCanaryEmails ?? [],
    };

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
    }

    private sealed class FakeVertex : IVertexAiClient
    {
        private readonly VertexGenerateResult? _result;
        private readonly Exception? _exception;
        private readonly bool _cancelWhenCalled;
        public int Calls { get; private set; }

        public FakeVertex(VertexGenerateResult result) => _result = result;
        public FakeVertex(Exception exception) => _exception = exception;
        public FakeVertex(bool cancelWhenCalled) => _cancelWhenCalled = cancelWhenCalled;

        public Task<VertexGenerateResult> GenerateAsync(
            string model,
            string location,
            string prompt,
            string? systemInstruction,
            int maxOutputTokens,
            System.Text.Json.JsonElement? responseJsonSchema,
            IReadOnlyList<InlineImageRequest>? images,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (_cancelWhenCalled)
                throw new OperationCanceledException(cancellationToken);
            if (_exception is not null)
                throw _exception;
            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeCatalog : ICatalogService
    {
        public bool KillSwitch { get; init; }
        private readonly PricingSnapshot _pricing = new(
            "test-v1", "gemini-test", 100_000, 200_000, 25_000, 200_000, 8_500, 10_000,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        public PricingSnapshot Pricing => _pricing;

        public Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSnapshot("test-v1", [], _pricing, KillSwitch, "disabled"));

        public Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken, bool allowPaymentCanary = false) =>
            throw new NotSupportedException();

        public long EstimateReservationCreditMicros(
            PricingSnapshot pricing,
            string prompt,
            int maxOutputTokens,
            long taskCapCreditMicros) => Math.Min(100_000, taskCapCreditMicros);

        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
            PricingSnapshot pricing,
            ProviderUsage usage) => (1_000, 20_000);

        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateLiveActualCost(
            PricingSnapshot pricing,
            ProviderUsage usage,
            int safetyBasisPoints) => (1_500, 30_000);
    }

    private sealed class FakeRouting(PricingSnapshot pricing) : IModelRoutingService
    {
        public Task<ModelRouteSnapshot> ResolveAsync(
            string? taskKind,
            string routingKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelRouteSnapshot(
                ModelRoutingService.ResolveRouteName(taskKind),
                "route-test-v1",
                "vertex",
                [new ModelCandidateSnapshot(pricing.Model, "test-region", pricing)],
                true,
                false,
                200_000,
                8_192,
                5,
                0,
                0,
                "1.3.0",
                "test"));
    }

    private sealed class FakeStore : IAccountStore
    {
        public long Available { get; set; }
        public bool ReserveCreated { get; set; } = true;
        public bool FailProviderCompletion { get; set; }
        public bool SettlementCancellationRequested { get; private set; }
        public List<string> Calls { get; } = [];
        public int ReserveCalls { get; private set; }
        public int ProviderStartedCalls { get; private set; }
        public int EnsureCapacityCalls { get; private set; }
        public int ProviderCompletedCalls { get; private set; }
        public int CheckpointCalls { get; private set; }
        public int SettleCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int ReconciliationCalls { get; private set; }
        public string? CompletedModel { get; private set; }
        public string? CompletedPricingVersion { get; private set; }
        public string? ReservedActivityType { get; private set; }

        public Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(Account(userId));

        public Task<UsagePageSnapshot> GetUsagePageAsync(
            string userId,
            int limit,
            string? cursor,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsagePageSnapshot([], null));

        public Task<ReservationResult> ReserveAsync(
            CallerIdentity caller, string requestId, string model, string pricingVersion,
            long reserveCreditMicros, long taskCapCreditMicros, int requestsPerMinute,
            int maxConcurrent, long globalDailyCap, CancellationToken cancellationToken,
            string activityType = "ai_request")
        {
            ReserveCalls++;
            ReservedActivityType = activityType;
            if (!ReserveCreated)
                return Task.FromResult(new ReservationResult(
                    false,
                    new Reservation(requestId, caller.UserId, 0, model, pricingVersion, "existing", DateTimeOffset.UtcNow),
                    Account(caller.UserId)));
            if (Available < reserveCreditMicros)
                throw new InvalidOperationException("No usable credits are available.");
            Available -= reserveCreditMicros;
            return Task.FromResult(new ReservationResult(true,
                new Reservation(requestId, caller.UserId, reserveCreditMicros, model, pricingVersion, "reserved", DateTimeOffset.UtcNow),
                Account(caller.UserId)));
        }

        public Task<SettlementResult> SettleAsync(
            string userId, string requestId, ProviderUsage usage, long actualCreditMicros,
            long providerCostUsdMicros, string outcome, CancellationToken cancellationToken)
        {
            SettleCalls++;
            Calls.Add("settle");
            SettlementCancellationRequested = cancellationToken.IsCancellationRequested;
            return Task.FromResult(new SettlementResult(true, actualCreditMicros, 0, Account(userId)));
        }

        public Task MarkReservationProviderStartedAsync(
            string userId,
            string requestId,
            CancellationToken cancellationToken)
        {
            ProviderStartedCalls++;
            Calls.Add("provider-started");
            return Task.CompletedTask;
        }

        public Task<long> EnsureReservationCapacityAsync(
            string userId,
            string requestId,
            long targetReservedCreditMicros,
            CancellationToken cancellationToken)
        {
            EnsureCapacityCalls++;
            return Task.FromResult(targetReservedCreditMicros);
        }

        public Task RecordProviderCompletionAsync(
            string userId,
            string requestId,
            ProviderUsage usage,
            long actualCreditMicros,
            long providerCostUsdMicros,
            string outcome,
            CancellationToken cancellationToken,
            string? model = null,
            string? pricingVersion = null)
        {
            ProviderCompletedCalls++;
            CompletedModel = model;
            CompletedPricingVersion = pricingVersion;
            Calls.Add("provider-completed");
            if (FailProviderCompletion)
                throw new InvalidOperationException("simulated provider evidence write failure");
            return Task.CompletedTask;
        }

        public Task CheckpointReservationUsageAsync(
            string userId,
            string requestId,
            ProviderUsage usage,
            long estimatedCreditMicros,
            long providerCostUsdMicros,
            CancellationToken cancellationToken)
        {
            CheckpointCalls++;
            return Task.CompletedTask;
        }

        public Task MarkReservationForReconciliationAsync(
            string userId,
            string requestId,
            string outcome,
            CancellationToken cancellationToken)
        {
            ReconciliationCalls++;
            return Task.CompletedTask;
        }

        public Task ReleaseReservationAsync(string userId, string requestId, string outcome, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }

        public Task<DesktopLoginAttempt> CreateLoginAttemptAsync(
            string stateHash, string deviceNonceHash, string pkceChallenge, string callbackUri,
            DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopLoginAttempt(
                "attempt-1", stateHash, deviceNonceHash, pkceChallenge, callbackUri, "pending", expiresAt));

        private AccountSnapshot Account(string userId) => new(
            userId, "person@example.test", "Test", "active", "none", Available, 0, 0, null, [], [], []);

        public Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeLoginAttemptAsync(string attemptId, string stateHash, string deviceNonceHash, string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DesktopSessionTokens> ExchangeLoginAttemptAsync(string attemptId, string stateHash, string deviceNonceHash, string authorizationCodeHash, string pkceVerifier, string deviceName, string deviceInstallationId, TimeSpan accessLifetime, TimeSpan refreshLifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CallerIdentity?> AuthenticateDesktopTokenAsync(string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DesktopSessionTokens> RefreshSessionAsync(string refreshToken, TimeSpan accessLifetime, TimeSpan refreshLifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeSessionAsync(string userId, string? sessionId, bool allDevices, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAccountOperationalDataAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountSnapshot> GrantCreditsAsync(string userId, long creditMicros, string reason, string idempotencyKey, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BeginPurchaseAsync(string userId, string purchaseId, CatalogProduct product, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AttachPurchaseProviderObjectAsync(string userId, string purchaseId, string providerObjectId, string providerObjectType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkPurchaseFailedAsync(string userId, string purchaseId, string outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelPendingPurchaseAsync(string userId, string purchaseId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkCheckoutSignatureVerifiedAsync(string userId, string purchaseId, string providerObjectId, string providerPaymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(string eventId, string eventType, string providerObjectId, string? providerPaymentId, string? providerOrderId, string? providerSubscriptionId, string? purchaseId, string? providerEventObjectId, long? providerAmountMinor, string status, CancellationToken cancellationToken, DateTimeOffset? subscriptionCycleEndsAt = null, string? providerCurrency = null, string? providerProductSku = null) => throw new NotSupportedException();
    }

    private sealed class RazorpayInspectionHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (path.StartsWith("v1/", StringComparison.Ordinal))
                path = path[3..];
            Paths.Add(path);
            var body = path switch
            {
                "orders/order_123" =>
                    """{"id":"order_123","status":"paid","amount":4900,"currency":"INR","receipt":"pur_123","notes":{"product_sku":"credits_test"}}""",
                "orders/order_123/payments" =>
                    """{"items":[{"id":"pay_123","status":"captured","amount":4900,"currency":"INR","order_id":"order_123","captured":true,"created_at":1785790000}]}""",
                "subscriptions/sub_123" =>
                    """{"id":"sub_123","status":"active","current_start":1785790000,"current_end":1788382000,"notes":{"purchase_id":"pur_123","product_sku":"monthly_test"}}""",
                "invoices?subscription_id=sub_123" =>
                    """{"items":[{"id":"inv_123","subscription_id":"sub_123","payment_id":"pay_123","status":"paid","issued_at":1785789900,"paid_at":1785790000}]}""",
                "payments/pay_123" =>
                    """{"id":"pay_123","status":"captured","amount":4900,"currency":"INR","order_id":"order_123","captured":true,"created_at":1785790000}""",
                "payments/pay_123/refunds?count=100&skip=0" =>
                    """{"items":[{"id":"rfnd_123","payment_id":"pay_123","status":"processed","amount":1000,"created_at":1785790100}]}""",
                "disputes" =>
                    """{"items":[{"id":"disp_123","payment_id":"pay_123","status":"open","amount":4900,"amount_deducted":0,"created_at":1785790200},{"id":"disp_other","payment_id":"pay_other","status":"open","amount":1,"amount_deducted":0}]}""",
                _ => throw new InvalidOperationException($"Unexpected Razorpay inspection path: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeAccessTokenProvider : IVertexAccessTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult("non-secret-test-token");
    }

    private sealed class BigQueryBillingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public bool SawProjectParameter { get; private set; }
        public bool SawClosedWindowParameters { get; private set; }
        public bool SawBytesBilledLimit { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("bigquery.googleapis.com", request.RequestUri?.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(RequestBody);
            var root = document.RootElement;
            SawBytesBilledLimit = root.GetProperty("maximumBytesBilled").GetString() == "10737418240";
            var query = root.GetProperty("query").GetString() ?? "";
            Assert.Contains("service.description = 'Vertex AI'", query, StringComparison.Ordinal);
            Assert.Contains("currency_conversion_rate", query, StringComparison.Ordinal);
            Assert.Contains("MAX(export_time)", query, StringComparison.Ordinal);
            Assert.Contains("keyboard-wtf-staging.billing_export.gcp_billing_export_v1_account", query, StringComparison.Ordinal);
            var names = root.GetProperty("queryParameters")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray();
            SawProjectParameter = names.Contains("project_id", StringComparer.Ordinal);
            SawClosedWindowParameters = names.Contains("window_start", StringComparer.Ordinal)
                && names.Contains("window_end", StringComparer.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"jobComplete":true,"rows":[{"f":[{"v":"123456"},{"v":"1785715200000000"}]}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class RazorpayPagedInspectionHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (path.StartsWith("v1/", StringComparison.Ordinal))
                path = path[3..];
            Paths.Add(path);

            var firstPage = path.EndsWith("skip=0", StringComparison.Ordinal);
            var items = firstPage
                ? string.Join(',', Enumerable.Range(0, 100).Select(index =>
                    $"{{\"id\":\"rfnd_{index}\",\"payment_id\":\"pay_123\",\"status\":\"processed\",\"amount\":1}}"))
                : path.EndsWith("skip=100", StringComparison.Ordinal)
                    ? "{\"id\":\"rfnd_last\",\"payment_id\":\"pay_123\",\"status\":\"processed\",\"amount\":1}"
                    : throw new InvalidOperationException($"Unexpected paged inspection path: {path}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"items\":[{items}]}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RazorpayWrongSubscriptionInvoiceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (path.StartsWith("v1/", StringComparison.Ordinal))
                path = path[3..];
            var body = path switch
            {
                "subscriptions/sub_123" =>
                    """{"id":"sub_123","status":"active","current_end":1788382000,"notes":{"purchase_id":"pur_123","product_sku":"monthly_test"}}""",
                "invoices?subscription_id=sub_123" =>
                    """{"items":[{"id":"inv_wrong","subscription_id":"sub_other","payment_id":"pay_123","status":"paid"}]}""",
                _ => throw new InvalidOperationException($"Unexpected Razorpay inspection path: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RazorpayTransientInspectionHandler(
        HttpStatusCode transientStatus,
        int failuresBeforeSuccess) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount <= failuresBeforeSuccess)
                return Task.FromResult(new HttpResponseMessage(transientStatus));

            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (path.StartsWith("v1/", StringComparison.Ordinal))
                path = path[3..];
            var body = path switch
            {
                "subscriptions/sub_123" =>
                    """{"id":"sub_123","status":"cancelled","current_end":null,"notes":{"purchase_id":"pur_123","product_sku":"monthly_test"}}""",
                "invoices?subscription_id=sub_123" =>
                    """{"items":[{"id":"inv_123","subscription_id":"sub_123","payment_id":null,"status":"cancelled"}]}""",
                _ => throw new InvalidOperationException($"Unexpected Razorpay inspection path: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
