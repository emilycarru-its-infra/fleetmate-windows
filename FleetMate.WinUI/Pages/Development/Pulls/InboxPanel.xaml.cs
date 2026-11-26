using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>The Inbox popover: GitHub notifications, Unread or All, with Mark Read, Done and Unsubscribe.</summary>
public sealed partial class InboxPanel : UserControl
{
    public InboxPanel() => InitializeComponent();

    public void Attach(DevelopmentPage page) { }

    public void Render() { }
}
