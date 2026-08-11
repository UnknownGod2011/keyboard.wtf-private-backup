param(
    [long]$MonthlyPricePaise = 99900,
    [long]$MonthlyAllowanceCreditMicros = 500000000,
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$usdToInrPaise = 10000L
$creditMicrosPerInrPaise = 10000L
$maximumProviderShareBasisPoints = 6000L

if ($MonthlyPricePaise -le 0 -or $MonthlyAllowanceCreditMicros -le 0) {
    throw "Price and allowance must be positive."
}

$rates = @{
    LiteInput = 300000L
    LiteOutput = 2500000L
    FlashInput = 1500000L
    FlashOutput = 9000000L
    LiveInputAudio = 3000000L
    LiveOutputAudio = 12000000L
}

function Get-TextCostUsdMicros {
    param(
        [long]$Events,
        [long]$InputTokens,
        [long]$OutputTokens,
        [long]$InputRate,
        [long]$OutputRate
    )
    $costPerEvent = ([decimal]$InputTokens * [decimal]$InputRate) `
        + ([decimal]$OutputTokens * [decimal]$OutputRate)
    return [decimal]$Events * $costPerEvent / [decimal]1000000
}

function Get-ProfileResult {
    param([System.Collections.IDictionary]$Profile)

    [decimal]$providerUsdMicros = 0
    $providerUsdMicros += Get-TextCostUsdMicros $Profile.FastEvents 800 250 $rates.LiteInput $rates.LiteOutput
    $providerUsdMicros += Get-TextCostUsdMicros $Profile.StrongEvents 2500 600 $rates.FlashInput $rates.FlashOutput
    $providerUsdMicros += Get-TextCostUsdMicros $Profile.BrowserEvents 3500 500 $rates.LiteInput $rates.LiteOutput
    $providerUsdMicros += Get-TextCostUsdMicros $Profile.ScreenEvents 5000 500 $rates.FlashInput $rates.FlashOutput

    # Conservative deterministic Live profile: 32 input-audio and 25 output-audio
    # tokens per second. Actual charging still uses provider usageMetadata.
    $liveSeconds = [long]$Profile.LiveMinutes * 60L
    $liveCostPerSecond = ([decimal]32 * [decimal]$rates.LiveInputAudio) `
        + ([decimal]25 * [decimal]$rates.LiveOutputAudio)
    $providerUsdMicros += [decimal]$liveSeconds * $liveCostPerSecond / [decimal]1000000

    $providerUsdMicros = [decimal]::Ceiling($providerUsdMicros)
    $providerCostPaise = [decimal]::Ceiling(
        $providerUsdMicros * [decimal]$usdToInrPaise / [decimal]1000000)
    $providerCostCreditMicros = [decimal]::Ceiling($providerCostPaise * $creditMicrosPerInrPaise)
    $coveragePercent = [decimal]::Round(
        [decimal]100 * [decimal]$MonthlyAllowanceCreditMicros / [Math]::Max([decimal]1, $providerCostCreditMicros),
        1)
    return [pscustomobject]@{
        Profile = $Profile.Name
        FastRequests = $Profile.FastEvents
        StrongRequests = $Profile.StrongEvents
        BrowserTasks = $Profile.BrowserEvents
        ScreenTasks = $Profile.ScreenEvents
        LiveMinutes = $Profile.LiveMinutes
        ProviderCostUsd = [decimal]::Round($providerUsdMicros / [decimal]1000000, 4)
        ProviderCostInr = [decimal]::Round($providerCostPaise / [decimal]100, 2)
        IncludedAllowanceCoveragePercent = $coveragePercent
        ExhaustsIncludedAllowance = $providerCostCreditMicros -gt $MonthlyAllowanceCreditMicros
    }
}

$profiles = @(
    [ordered]@{ Name = "LIGHT"; FastEvents = 200L; StrongEvents = 10L; BrowserEvents = 10L; ScreenEvents = 3L; LiveMinutes = 5L },
    [ordered]@{ Name = "NORMAL"; FastEvents = 600L; StrongEvents = 30L; BrowserEvents = 60L; ScreenEvents = 15L; LiveMinutes = 30L },
    [ordered]@{ Name = "HEAVY"; FastEvents = 3000L; StrongEvents = 200L; BrowserEvents = 400L; ScreenEvents = 120L; LiveMinutes = 180L },
    [ordered]@{ Name = "LIVE_VOICE_HEAVY"; FastEvents = 600L; StrongEvents = 30L; BrowserEvents = 30L; ScreenEvents = 10L; LiveMinutes = 600L },
    [ordered]@{ Name = "BROWSER_SCREEN_HEAVY"; FastEvents = 600L; StrongEvents = 60L; BrowserEvents = 1800L; ScreenEvents = 300L; LiveMinutes = 30L },
    [ordered]@{ Name = "ABUSE"; FastEvents = 10000L; StrongEvents = 2000L; BrowserEvents = 5000L; ScreenEvents = 2000L; LiveMinutes = 2000L }
)

$results = @($profiles | ForEach-Object { Get-ProfileResult $_ })
$providerBudgetPaise = [decimal]::Ceiling(
    [decimal]$MonthlyAllowanceCreditMicros / $creditMicrosPerInrPaise)
$providerShareBasisPoints = [decimal]::Ceiling(
    [decimal]10000 * $providerBudgetPaise / [decimal]$MonthlyPricePaise)
if ($providerShareBasisPoints -gt $maximumProviderShareBasisPoints) {
    throw "The launch allowance exceeds the approved 60% provider-cost share."
}

$summary = [pscustomobject]@{
    CatalogVersion = "launch-monthly-inr999-v1"
    PricingVersion = "vertex-standard-global-fx100-2026-08-11"
    MonthlyPriceInr = [decimal]$MonthlyPricePaise / [decimal]100
    MonthlyAllowanceCredits = [decimal]$MonthlyAllowanceCreditMicros / [decimal]1000000
    MaximumProviderCostInr = $providerBudgetPaise / [decimal]100
    ProviderCostSharePercent = [decimal]::Round($providerShareBasisPoints / [decimal]100, 2)
    Profiles = $results
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 6
} else {
    $summary | Select-Object CatalogVersion, PricingVersion, MonthlyPriceInr,
        MonthlyAllowanceCredits, MaximumProviderCostInr, ProviderCostSharePercent | Format-List
    $results | Format-Table -AutoSize
}
