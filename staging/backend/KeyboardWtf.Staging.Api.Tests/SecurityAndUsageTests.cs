using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class SecurityAndUsageTests
{
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
            var provider = new RazorpayPaymentProvider(new HttpClient());
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

    [Fact]
    public void Partial_refund_reverses_only_the_proportional_credit()
    {
        Assert.Equal(
            250_000,
            FirestoreAccountStore.ReversalCreditMicros("refund.processed", 25, 100, 1_000_000));
        Assert.Equal(
            1_000_000,
            FirestoreAccountStore.ReversalCreditMicros("payment.dispute.created", null, 100, 1_000_000));
        Assert.Equal(
            0,
            FirestoreAccountStore.ReversalCreditMicros("refund.processed", null, 100, 1_000_000));
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
        Assert.Equal(1, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
    }

    [Fact]
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
                store.ReserveAsync(first, requestId, "gemini-test", "test", 100_000, 500_000, 12, 1, 10_000_000_000, CancellationToken.None),
                store.ReserveAsync(second, requestId, "gemini-test", "test", 100_000, 500_000, 12, 1, 10_000_000_000, CancellationToken.None));
            Assert.All(reservations, result => Assert.True(result.Created));
            Assert.Equal(900_000, reservations[0].Account.AvailableCreditMicros);
            Assert.Equal(1_900_000, reservations[1].Account.AvailableCreditMicros);

            var zeroUsage = new ProviderUsage(0, 0, 0, 0, 0, 0, 0, "integration-test");
            var settlements = await Task.WhenAll(
                store.SettleAsync(first.UserId, requestId, zeroUsage, 0, 0, "verified", CancellationToken.None),
                store.SettleAsync(second.UserId, requestId, zeroUsage, 0, 0, "verified", CancellationToken.None));
            Assert.All(settlements, result => Assert.True(result.Settled));
            Assert.Equal(1_000_000, settlements[0].Account.AvailableCreditMicros);
            Assert.Equal(2_000_000, settlements[1].Account.AvailableCreditMicros);
            Assert.Single(settlements[0].Account.RecentUsage);
            Assert.Single(settlements[1].Account.RecentUsage);
        }
        finally
        {
            await DeleteIntegrationAccountAsync(db, first.UserId);
            await DeleteIntegrationAccountAsync(db, second.UserId);
        }
    }

    [Fact]
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
                        10_000_000_000,
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
        var paymentOne = $"pay_one_{suffix}";
        var paymentTwo = $"pay_two_{suffix}";
        try
        {
            var unmatched = await store.ApplyPaymentWebhookAsync(
                $"evt_unmatched_{suffix}",
                "order.paid",
                orderId,
                paymentOne,
                orderId,
                null,
                null,
                paymentOne,
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

            var replay = await store.ApplyPaymentWebhookAsync(
                $"evt_unmatched_{suffix}",
                "order.paid",
                orderId,
                paymentOne,
                orderId,
                null,
                creditPurchaseId,
                paymentOne,
                4_900,
                "paid",
                CancellationToken.None);
            Assert.Equal("credited", replay.Outcome);
            await store.ApplyPaymentWebhookAsync(
                $"evt_same_payment_{suffix}",
                "payment.captured",
                orderId,
                paymentOne,
                orderId,
                null,
                creditPurchaseId,
                paymentOne,
                4_900,
                "captured",
                CancellationToken.None);
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

            await store.ApplyPaymentWebhookAsync(
                $"evt_charge_one_{suffix}", "subscription.charged", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, paymentOne, 9_900, "active", CancellationToken.None);
            await store.ApplyPaymentWebhookAsync(
                $"evt_charge_one_replay_{suffix}", "subscription.charged", subscriptionId,
                paymentOne, null, subscriptionId, subscriptionPurchaseId, paymentOne, 9_900, "active", CancellationToken.None);
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
                10_000_000_000,
                CancellationToken.None);
            await store.MarkReservationProviderStartedAsync(caller.UserId, usageRequest, CancellationToken.None);
            await store.SettleAsync(
                caller.UserId,
                usageRequest,
                new ProviderUsage(10, 10, 0, 0, 0, 0, 20, "provider-cycle-test"),
                10_000_000,
                1_000,
                "completed",
                CancellationToken.None);
            Assert.Equal(10_000_000, (await store.GetAccountAsync(caller.UserId, CancellationToken.None)).CycleUsageCreditMicros);

            var nextCycleEnd = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
            await store.ApplyPaymentWebhookAsync(
                $"evt_charge_two_{suffix}", "subscription.charged", subscriptionId,
                paymentTwo, null, subscriptionId, subscriptionPurchaseId, paymentTwo, 9_900, "active", CancellationToken.None,
                nextCycleEnd);
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
            Assert.Equal(90_000_000, refunded.AvailableCreditMicros);
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
        var service = CreateAi(store, new FakeVertex(new VertexRequestRejectedException(429)));
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
    public void Live_proxy_replaces_only_the_untrusted_model_identity()
    {
        const string expectedModel =
            "projects/keyboard-wtf-staging/locations/us-central1/publishers/google/models/gemini-live-test";
        var rewritten = VertexLiveProxyService.RewriteSetup(
            """{"setup":{"model":"models/untrusted","generationConfig":{"responseModalities":["AUDIO"]},"tools":[{"functionDeclarations":[]}]}}""",
            expectedModel);
        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal(expectedModel, document.RootElement.GetProperty("setup").GetProperty("model").GetString());
        Assert.Equal(
            "AUDIO",
            document.RootElement.GetProperty("setup").GetProperty("generationConfig")
                .GetProperty("responseModalities")[0].GetString());
        Assert.True(document.RootElement.GetProperty("setup").TryGetProperty("tools", out _));
    }

    [Fact]
    public void Live_proxy_rejects_a_non_setup_first_message() =>
        Assert.Throws<ArgumentException>(() => VertexLiveProxyService.RewriteSetup(
            """{"clientContent":{"turnComplete":true}}""",
            "projects/test/locations/test/publishers/google/models/test"));

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
        Assert.Equal(2, snapshot.AudioTokens);
        Assert.Equal(2, snapshot.TotalTokens);
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
    }

    private static readonly CallerIdentity Caller = new(
        "uid-1", "person@example.test", "Test Person", "firebase");

    private static AiUsageService CreateAi(
        FakeStore store,
        FakeVertex vertex,
        FakeCatalog? catalog = null) =>
        new(store, catalog ?? new FakeCatalog(), vertex, Options.Create(TestOptions()));

    private static async Task DeleteIntegrationAccountAsync(FirestoreDb db, string userId)
    {
        var account = db.Collection("accounts").Document(userId);
        foreach (var collectionName in new[] { "ledger", "reservations", "usage", "purchases", "devices", "sessions" })
        {
            var documents = await account.Collection(collectionName).Limit(100).GetSnapshotAsync();
            if (documents.Count == 0)
                continue;
            var batch = db.StartBatch();
            foreach (var document in documents.Documents)
                batch.Delete(document.Reference);
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
        await account.DeleteAsync();
    }

    private static PaidSystemOptions TestOptions() => new()
    {
        ProjectId = "keyboard-wtf-staging",
        Region = "asia-south1",
        VertexModel = "gemini-test",
        WebBaseUrl = "https://staging.example.test",
        AllowedOrigins = ["https://staging.example.test"],
        MaxRequestSeconds = 5,
        DefaultTaskCreditMicrosCap = 25_000_000,
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

        public Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSnapshot("test-v1", [], _pricing, KillSwitch, "disabled"));

        public Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public long EstimateReservationCreditMicros(
            PricingSnapshot pricing,
            string prompt,
            int maxOutputTokens,
            long taskCapCreditMicros) => Math.Min(100_000, taskCapCreditMicros);

        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
            PricingSnapshot pricing,
            ProviderUsage usage) => (1_000, 20_000);
    }

    private sealed class FakeStore : IAccountStore
    {
        public long Available { get; set; }
        public bool ReserveCreated { get; set; } = true;
        public bool SettlementCancellationRequested { get; private set; }
        public int ReserveCalls { get; private set; }
        public int ProviderStartedCalls { get; private set; }
        public int SettleCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int ReconciliationCalls { get; private set; }

        public Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(Account(userId));

        public Task<ReservationResult> ReserveAsync(
            CallerIdentity caller, string requestId, string model, string pricingVersion,
            long reserveCreditMicros, long taskCapCreditMicros, int requestsPerMinute,
            int maxConcurrent, long globalDailyCap, CancellationToken cancellationToken)
        {
            ReserveCalls++;
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
            SettlementCancellationRequested = cancellationToken.IsCancellationRequested;
            return Task.FromResult(new SettlementResult(true, actualCreditMicros, 0, Account(userId)));
        }

        public Task MarkReservationProviderStartedAsync(
            string userId,
            string requestId,
            CancellationToken cancellationToken)
        {
            ProviderStartedCalls++;
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
        public Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountSnapshot> GrantCreditsAsync(string userId, long creditMicros, string reason, string idempotencyKey, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BeginPurchaseAsync(string userId, string purchaseId, CatalogProduct product, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AttachPurchaseProviderObjectAsync(string userId, string purchaseId, string providerObjectId, string providerObjectType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkPurchaseFailedAsync(string userId, string purchaseId, string outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelPendingPurchaseAsync(string userId, string purchaseId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkCheckoutSignatureVerifiedAsync(string userId, string purchaseId, string providerObjectId, string providerPaymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(string eventId, string eventType, string providerObjectId, string? providerPaymentId, string? providerOrderId, string? providerSubscriptionId, string? purchaseId, string? providerEventObjectId, long? providerAmountMinor, string status, CancellationToken cancellationToken, DateTimeOffset? subscriptionCycleEndsAt = null) => throw new NotSupportedException();
    }
}
