using System.Globalization;

namespace Pollux.Copilot.Models;

/// <summary>How much of the premium requests is left; drives the colour of the Copilot Panel and status bar item.</summary>
public enum CopilotQuotaLevel
{
    Ok,
    Low,
    Exhausted,
    Unlimited,
}

/// <summary>The premium-request quota of the signed-in account (from the CLI's <c>account.getQuota</c>).</summary>
/// <param name="Unlimited">The account has an unlimited entitlement.</param>
/// <param name="Entitlement">Requests included per period; -1 when unlimited.</param>
/// <param name="Used">Requests used this period.</param>
/// <param name="RemainingPercent">Percentage of the entitlement that is left.</param>
/// <param name="ResetDate">Date the quota resets, if known.</param>
/// <param name="UsageAllowedWhenExhausted">Further requests are still permitted (billed overage).</param>
public sealed record CopilotQuota(bool Unlimited, int Entitlement, int Used, double RemainingPercent, DateTimeOffset? ResetDate, bool UsageAllowedWhenExhausted)
{
    /// <summary>Below this share of the entitlement the quota counts as low.</summary>
    private const double LowPercent = 10;

    public int Remaining => Math.Max(0, Entitlement - Used);

    /// <summary>Nothing is left and the CLI will refuse further premium requests.</summary>
    public bool Exhausted => !Unlimited && Entitlement >= 0 && RemainingPercent <= 0 && !UsageAllowedWhenExhausted;

    public CopilotQuotaLevel Level =>
        Unlimited ? CopilotQuotaLevel.Unlimited
        : Exhausted ? CopilotQuotaLevel.Exhausted
        : Entitlement >= 0 && RemainingPercent <= LowPercent ? CopilotQuotaLevel.Low
        : CopilotQuotaLevel.Ok;

    /// <summary>The sentence shown in the Copilot Panel.</summary>
    public string Text
    {
        get
        {
            if (Unlimited) return "Unlimited premium requests";
            var text = string.Create(CultureInfo.InvariantCulture, $"{Remaining} of {Entitlement} premium requests left");
            return ResetDay is { } day ? $"{text} (resets {day})" : text;
        }
    }

    /// <summary>The compact form for the status bar.</summary>
    public string Compact => Unlimited ? "unlimited" : string.Create(CultureInfo.InvariantCulture, $"{Remaining}/{Entitlement}");

    private string? ResetDay => ResetDate?.ToString("d MMM", CultureInfo.InvariantCulture);
}
