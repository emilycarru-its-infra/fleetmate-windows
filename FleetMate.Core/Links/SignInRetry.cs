namespace FleetMate.Core.Links;

/// <summary>Where sign-in to a service stands, as far as a view waiting on it cares.</summary>
public enum SignInStatus
{
    /// <summary>The service isn't set up for sign-in, so sign-in can never be what fixes a failure.</summary>
    NotConfigured,
    /// <summary>No sign-in has finished yet: the app is starting, or an attempt is running.</summary>
    SigningIn,
    SignedIn,
    /// <summary>The last sign-in attempt failed. A later one (Retry, Settings) can still succeed.</summary>
    Failed,
}

/// <summary>
/// What a view does with a request that failed — the decision behind a link
/// that opens before sign-in has finished.
/// </summary>
/// <remarks>
/// A link that launches the app reaches its view within a second, long before
/// the silent sign-in completes, so the first request fails as not
/// authenticated. Reporting that as final left the view stuck on an error that
/// sign-in fixes a moment later.
/// </remarks>
public enum SignInRetry
{
    /// <summary>Sign-in isn't the cause: report the failure.</summary>
    ShowError,
    /// <summary>
    /// Sign-in finished while the request was in flight: run it again now,
    /// since the sign-in event that would have re-run it has already passed.
    /// </summary>
    RetryNow,
    /// <summary>Hold the request and run it again once sign-in completes.</summary>
    WaitForSignIn,
}

/// <summary>What a Retry button does after a request failed.</summary>
/// <remarks>A Retry that only reloaded while signed out failed again the same way.</remarks>
public enum SignInRetryTap
{
    /// <summary>Reload now.</summary>
    Reload,
    /// <summary>Start the silent sign-in, hold the request, and reload once sign-in completes.</summary>
    SignInThenReload,
    /// <summary>A sign-in attempt is already running: hold the request until it finishes.</summary>
    WaitForSignIn,
}

public static class SignInDecisions
{
    /// <param name="startedSignedIn">Whether the service was signed in when the request began.</param>
    /// <param name="status">Where sign-in stands now that the request has failed.</param>
    public static SignInRetry AfterFailure(bool startedSignedIn, SignInStatus status) => status switch
    {
        SignInStatus.NotConfigured => SignInRetry.ShowError,
        SignInStatus.SignedIn => startedSignedIn ? SignInRetry.ShowError : SignInRetry.RetryNow,
        _ => SignInRetry.WaitForSignIn,
    };

    public static SignInRetryTap OnRetry(SignInStatus status) => status switch
    {
        SignInStatus.Failed => SignInRetryTap.SignInThenReload,
        SignInStatus.SigningIn => SignInRetryTap.WaitForSignIn,
        _ => SignInRetryTap.Reload,
    };

    /// <summary>
    /// Whether opening the link reads from Azure DevOps, and so has to wait for
    /// Azure DevOps sign-in when it arrives first. GitHub links never wait.
    /// </summary>
    public static bool NeedsAzureDevOps(this FleetMateLink link) => link switch
    {
        FleetMateLink.PullRequest { Source: FleetMateLink.AzureDevOps } => true,
        FleetMateLink.Commit { Source: FleetMateLink.AzureDevOps } => true,
        FleetMateLink.AzureDevOpsRun or FleetMateLink.AzureDevOpsPipeline or FleetMateLink.WorkItem => true,
        _ => false,
    };
}
