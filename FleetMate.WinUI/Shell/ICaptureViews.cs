namespace FleetMate.WinUI.Shell;

/// <summary>
/// A page with views of its own beyond what it opens on — sub-tabs, a selected
/// item's detail — so the QA capture (<see cref="CaptureHarness"/>) shows each
/// one, not only the first. Each view is captured as <c>&lt;tab&gt;-&lt;view&gt;</c>.
/// </summary>
public interface ICaptureViews
{
    /// <summary>The views to capture, in order; short lowercase names such as "pulls" or "pulls-detail".</summary>
    IReadOnlyList<string> CaptureViews { get; }

    /// <summary>Show <paramref name="view"/> and finish loading it.</summary>
    Task ShowCaptureViewAsync(string view);
}
