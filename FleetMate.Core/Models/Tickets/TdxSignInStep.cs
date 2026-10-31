namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// Where the launch sign-in has got to, for the progress shown over an empty
/// ticket list. Sign-in is fully automatic; these only report it.
/// </summary>
public enum TdxSignInStep
{
    /// <summary>Resolving the Windows account the session must belong to.</summary>
    FindingAccount,
    /// <summary>The Windows-credential (Negotiate) chain through Microsoft sign-in.</summary>
    ConnectingToMicrosoft,
    /// <summary>Asking TeamDynamix for, or checking, the session.</summary>
    SigningInToTeamDynamix,
    /// <summary>The hidden browser fallback.</summary>
    FinishingSignIn,
    /// <summary>Signed in; waiting for the first ticket list.</summary>
    LoadingTickets,
}

public static class TdxSignInSteps
{
    public static string Label(this TdxSignInStep step) => step switch
    {
        TdxSignInStep.FindingAccount => "Finding your account",
        TdxSignInStep.ConnectingToMicrosoft => "Connecting to Microsoft",
        TdxSignInStep.SigningInToTeamDynamix => "Signing in to TeamDynamix",
        TdxSignInStep.FinishingSignIn => "Finishing sign-in",
        _ => "Loading tickets",
    };

    /// <summary>How full the bar is while this step runs: each step fills its share.</summary>
    public static double Fraction(this TdxSignInStep step) =>
        ((int)step + 1) / (double)(Enum.GetValues<TdxSignInStep>().Length + 1);
}
