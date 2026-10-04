using FleetMate.GUI.Knowledge;

namespace FleetMate.GUI;

/// <summary>The Handbook copy that the asset detail's Handbook card reads.</summary>
public partial class App
{
    public HandbookStore? Handbook { get; private set; }

    /// <summary>Start keeping the Handbook current, when its repository is configured.</summary>
    private void StartHandbook()
    {
        Handbook = new HandbookStore(Config, async () =>
            DevOpsSsoService is { } sso ? await sso.GetValidTokenAsync() : null);
        Handbook.Start();
    }
}
