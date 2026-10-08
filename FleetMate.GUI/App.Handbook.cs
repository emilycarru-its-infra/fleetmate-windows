using FleetMate.Core.Knowledge;
using FleetMate.GUI.Knowledge;

namespace FleetMate.GUI;

/// <summary>The Handbook and the shared agent skills, from FleetMate's own copies of their repositories.</summary>
public partial class App
{
    public HandbookStore? Handbook { get; private set; }
    public SkillsStore? Skills { get; private set; }

    private Views.Knowledge.HandbookReaderWindow? _handbookReader;

    /// <summary>Start keeping the Handbook and the agents hub current, when their repositories are configured.</summary>
    private void StartHandbook()
    {
        Func<Task<string?>> token = async () =>
            DevOpsSsoService is { } sso ? await sso.GetValidTokenAsync() : null;
        Handbook = new HandbookStore(Config, token);
        Handbook.Start();
        Skills = new SkillsStore(Config, token);
        Skills.Start();
    }

    /// <summary>
    /// Read a Handbook page inside FleetMate. One reader window is reused, so
    /// following several cards does not pile up windows.
    /// </summary>
    public void OpenHandbookPage(HandbookPage page)
    {
        if (Handbook is not { } store) return;
        var full = store.FullPage(page);
        if (_handbookReader is not { IsLoaded: true })
        {
            _handbookReader = new Views.Knowledge.HandbookReaderWindow { Owner = MainWindow };
            _handbookReader.Closed += (_, _) => _handbookReader = null;
        }
        _handbookReader.Show(full, store.SiteUrl(full));
    }
}
