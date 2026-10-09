namespace Pollux.Copilot.Connection;

/// <summary>The account as the CLI reports it. Pollux never sees a token.</summary>
/// <param name="Problem">Why the account is not usable, as reported by the CLI (used to tell "no seat" from "not signed in").</param>
public sealed record CopilotAccountStatus(bool IsAuthenticated, string? Login = null, string? Problem = null, bool QuotaExhausted = false);

/// <summary>A connection that can tell who is signed in; implemented by the real CLI connection and by test fakes.</summary>
public interface ICopilotAccountSource
{
    Task<CopilotAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Maps CLI error text to the connection state that explains it.</summary>
public static class CopilotProblems
{
    public static CopilotConnectionIssue Classify(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (Has(t, "quota", "rate limit", "429", "premium request")) return CopilotConnectionIssue.QuotaExhausted;
        if (Has(t, "proxy", "certificate", "cert_", "self signed", "self-signed", "ssl", "tls", "econnrefused", "enotfound", "etimedout", "econnreset", "network", "getaddrinfo"))
            return CopilotConnectionIssue.Network;
        if (Has(t, "policy", "disabled by", "administrator")) return CopilotConnectionIssue.OrgPolicy;
        if (Has(t, "subscription", "seat", "not have access", "no access", "not enabled for copilot", "403"))
            return CopilotConnectionIssue.NoSeat;
        if (Has(t, "not authenticated", "not logged in", "not signed in", "unauthorized", "401", "sign in", "login"))
            return CopilotConnectionIssue.NotSignedIn;
        return CopilotConnectionIssue.CliFailed;
    }

    public static bool LooksMissing(string? text) =>
        Has((text ?? "").ToLowerInvariant(), "missing", "does not exist", "not found");

    private static bool Has(string text, params string[] needles) => needles.Any(text.Contains);
}

/// <summary>
/// Works out which Copilot Panel state applies (spec "Process &amp; auth"): starts the CLI through the
/// <see cref="CopilotHost"/> on first use, asks the CLI for the account and classifies every failure.
/// </summary>
public sealed class CopilotConnectionMonitor(CopilotHost host)
{
    public async Task<CopilotConnectionState> CheckAsync(CancellationToken cancellationToken)
    {
        var connection = await host.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return host.State.Status switch
            {
                CopilotHostStatus.Disabled or CopilotHostStatus.Stopped => CopilotConnectionState.For(CopilotConnectionIssue.Disabled),
                _ => FromProblem(host.State.Reason, startFailure: true),
            };
        }

        if (connection is not ICopilotAccountSource source)
        {
            return CopilotConnectionState.Ready;
        }

        CopilotAccountStatus account;
        try
        {
            account = await source.GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure talking to the CLI is a state the Panel shows.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return FromProblem(ex.Message, startFailure: false);
        }

        if (!account.IsAuthenticated)
        {
            var issue = CopilotProblems.Classify(account.Problem);
            return CopilotConnectionState.For(issue is CopilotConnectionIssue.CliFailed ? CopilotConnectionIssue.NotSignedIn : issue, account.Problem);
        }

        return account.QuotaExhausted
            ? CopilotConnectionState.For(CopilotConnectionIssue.QuotaExhausted)
            : CopilotConnectionState.Ready;
    }

    private static CopilotConnectionState FromProblem(string? text, bool startFailure)
    {
        if (startFailure && CopilotProblems.LooksMissing(text))
        {
            return CopilotConnectionState.For(CopilotConnectionIssue.CliMissing, text);
        }

        return CopilotConnectionState.For(CopilotProblems.Classify(text), text);
    }
}
