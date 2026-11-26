using FleetMate.Core.Inventory;
using FleetMate.Core.Models.Inventory;
using FleetMate.WinUI.Pages.Development;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>Theme brushes and the agent-context hook the Inventory views share.</summary>
internal static class InventoryUi
{
    /// <summary>
    /// The status colour: deployed the success fill, ready to deploy the
    /// attention fill, pending the caution fill, the rest neutral. System fill
    /// brushes, so Light, Dark and High Contrast each get theirs.
    /// </summary>
    public static Brush StatusBrush(SnipeStatusLabel? status) => DevelopmentUi.Theme(AssetDetailLayout.StatusTone(status) switch
    {
        AssetStatusTone.Deployed => "SystemFillColorSuccessBrush",
        AssetStatusTone.Deployable => "SystemFillColorAttentionBrush",
        AssetStatusTone.Pending => "SystemFillColorCautionBrush",
        _ => "SystemFillColorNeutralBrush",
    });

    public static Brush Secondary => DevelopmentUi.Theme("TextFillColorSecondaryBrush");
    public static Brush Primary => DevelopmentUi.Theme("TextFillColorPrimaryBrush");

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>
    /// The selected asset, written to the FLEETMATE_CONTEXT file terminal
    /// sessions read, as FleetMate.GUI's ContextPublisher does. A no-op until
    /// the Terminal tab is ported and sets <c>App.Services.AgentContext</c>.
    /// </summary>
    public static void PublishContext(SnipeAsset? asset) =>
        App.Services?.AgentContext?.SetSelection("asset", AssetContext.Selection(asset));
}
