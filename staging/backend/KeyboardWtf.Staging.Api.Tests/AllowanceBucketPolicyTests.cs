using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class AllowanceBucketPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Subscription_allowance_is_consumed_before_non_expiring_topups()
    {
        var account = EmptyAccount();
        var wallet = AllowanceWallet.FromAccount(account, Now);
        var debt = 0L;
        var topup = wallet.AddCredit("topup", "razorpay-payment", "pay_topup", 1_000, 0, Now, ref debt);
        var subscription = wallet.AddCredit(
            "subscription",
            "razorpay-payment",
            "pay_subscription",
            500,
            Now.AddDays(20).ToUnixTimeSeconds(),
            Now,
            ref debt);

        var allocations = wallet.Reserve(600);

        Assert.Equal(subscription, allocations[0].BucketId);
        Assert.Equal(500, allocations[0].CreditMicros);
        Assert.Equal(topup, allocations[1].BucketId);
        Assert.Equal(100, allocations[1].CreditMicros);
    }

    [Fact]
    public void Expired_subscription_allowance_disappears_but_topup_remains()
    {
        var account = EmptyAccount();
        var wallet = AllowanceWallet.FromAccount(account, Now);
        var debt = 0L;
        wallet.AddCredit("topup", "razorpay-payment", "pay_topup", 1_000, 0, Now, ref debt);
        wallet.AddCredit(
            "subscription",
            "razorpay-payment",
            "pay_subscription",
            500,
            Now.AddMinutes(1).ToUnixTimeSeconds(),
            Now,
            ref debt);
        account["allowanceBuckets"] = wallet.ToFirestoreMap();
        account["availableCreditMicros"] = wallet.AvailableCreditMicros;

        var afterExpiry = AllowanceWallet.FromAccount(account, Now.AddMinutes(2));

        Assert.Equal(1_000, afterExpiry.AvailableCreditMicros);
        Assert.Single(afterExpiry.NewlyExpired);
        Assert.Equal(500, afterExpiry.NewlyExpired[0].CreditMicros);
    }

    [Fact]
    public void Releasing_after_cycle_expiry_does_not_restore_expired_allowance()
    {
        var account = EmptyAccount();
        var wallet = AllowanceWallet.FromAccount(account, Now);
        var debt = 0L;
        wallet.AddCredit(
            "subscription",
            "razorpay-payment",
            "pay_subscription",
            500,
            Now.AddMinutes(1).ToUnixTimeSeconds(),
            Now,
            ref debt);
        var allocations = wallet.Reserve(400);
        account["allowanceBuckets"] = wallet.ToFirestoreMap();
        account["availableCreditMicros"] = wallet.AvailableCreditMicros;
        account["reservedCreditMicros"] = wallet.ReservedCreditMicros;

        var afterExpiry = AllowanceWallet.FromAccount(account, Now.AddMinutes(2));
        var released = afterExpiry.Release(allocations);

        Assert.Equal(0, released);
        Assert.Equal(0, afterExpiry.AvailableCreditMicros);
        Assert.Equal(0, afterExpiry.ReservedCreditMicros);
    }

    [Fact]
    public void Refund_reverses_only_its_payment_bucket_and_creates_debt_for_consumed_credit()
    {
        var account = EmptyAccount();
        var wallet = AllowanceWallet.FromAccount(account, Now);
        var debt = 0L;
        var first = wallet.AddCredit("topup", "razorpay-payment", "pay_first", 500, 0, Now, ref debt);
        var second = wallet.AddCredit("topup", "razorpay-payment", "pay_second", 700, 0, Now.AddSeconds(1), ref debt);
        var allocation = wallet.Reserve(300);
        wallet.Settle(allocation, 300);

        var reversed = wallet.Reverse("razorpay-payment", "pay_first", 500, ref debt);

        Assert.Equal(500, reversed);
        Assert.Equal(300, debt);
        Assert.Equal(700, wallet.AvailableCreditMicros);
        Assert.Equal(500, wallet.Buckets.Single(bucket => bucket.Id == first).ReversedCreditMicros);
        Assert.Equal(700, wallet.Buckets.Single(bucket => bucket.Id == second).AvailableCreditMicros);
    }

    [Fact]
    public void Legacy_aggregate_balance_is_migrated_without_loss()
    {
        var account = EmptyAccount();
        account["availableCreditMicros"] = 700L;
        account["reservedCreditMicros"] = 100L;

        var wallet = AllowanceWallet.FromAccount(account, Now);

        Assert.Equal(700, wallet.AvailableCreditMicros);
        Assert.Equal(100, wallet.ReservedCreditMicros);
        Assert.Contains(wallet.Buckets, bucket => bucket.Kind == "legacy");
    }

    private static Dictionary<string, object> EmptyAccount() => new()
    {
        ["availableCreditMicros"] = 0L,
        ["reservedCreditMicros"] = 0L,
        ["allowanceBuckets"] = new Dictionary<string, object>(),
    };
}
