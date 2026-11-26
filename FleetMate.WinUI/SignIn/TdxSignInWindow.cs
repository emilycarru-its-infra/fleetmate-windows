using FleetMate.Core.Services.SignIn;
using FleetMate.Core.Services.Tickets;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.SignIn;

/// <summary>
/// The one visible sign-in: TeamDynamix, opened by Sign In when Entra wants a
/// second factor that a hidden browser cannot give. The page handling is
/// <see cref="TdxWindowSignIn"/>, shared with the WPF app. The window closes
/// itself once signed in, after <see cref="TdxSessionPolicy.WindowTimeout"/>,
/// or when a token arrives from elsewhere (the cancellation token).
/// </summary>
internal sealed class TdxSignInWindow : Window
{
    private readonly string _baseUrl;
    private readonly string _upn;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 12, 16, 12), TextWrapping = TextWrapping.Wrap };
    private TdxWindowSignIn? _flow;

    public TdxSsoResult? Result { get; private set; }

    private TdxSignInWindow(string baseUrl, string upn)
    {
        _baseUrl = baseUrl;
        _upn = upn;
        Title = "Sign in to TeamDynamix";
        _status.Text = TdxWindowSignIn.Prompt(upn);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_web, "TeamDynamix sign-in");
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        Grid.SetRow(_web, 1);
        root.Children.Add(_status);
        root.Children.Add(_web);
        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        Closed += (_, _) => Finish(TdxSsoResult.Failed("The sign-in window was closed before signing in"));
    }

    /// <summary>Show the window and wait for it to finish; null when no window could open.</summary>
    public static async Task<TdxSsoResult?> SignInAsync(string baseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync(ct);
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address; the sign-in window is not opened");
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        }
        if (ct.IsCancellationRequested) return null;

        var window = new TdxSignInWindow(baseUrl, upn);
        var done = new TaskCompletionSource<TdxSsoResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window._finished = result => done.TrySetResult(result);
        var queue = window.DispatcherQueue;
        using var cancel = ct.Register(() => queue.TryEnqueue(() =>
        {
            Log.Information("[tdx-sso] A TeamDynamix token arrived; closing the sign-in window");
            window.Close();
        }));
        var timer = queue.CreateTimer();
        timer.Interval = timeout;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Log.Warning("[tdx-sso] The sign-in window timed out after {Timeout}; closing it", timeout);
            window.Finish(TdxSsoResult.Failed("The sign-in window timed out"));
        };

        Log.Information("[tdx-sso] Opening the sign-in window for {Upn}", upn);
        timer.Start();
        window.Activate();
        _ = window.StartAsync();
        var result = await done.Task;
        timer.Stop();
        Log.Information("[tdx-sso] Sign-in window closed: {Outcome}",
            result is { Success: true } ? "signed in" : result?.Error ?? "closed before signing in");
        return result;
    }

    private Action<TdxSsoResult?>? _finished;

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await SignInEnvironment.GetAsync());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] The sign-in window's browser could not start");
            Finish(TdxSsoResult.Failed($"The sign-in browser could not start: {ex.Message}"));
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        // A sign-in page that opens a window stays in this one.
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        _flow = new TdxWindowSignIn(new CoreWebView2SignInBrowser(core), _baseUrl, _upn);
        _flow.StatusChanged += status => _status.Text = status;
        _flow.Finished += Finish;
        _flow.Start();
    }

    private void Finish(TdxSsoResult result)
    {
        if (Result != null) return;
        Result = result;
        _flow?.Finish(result);
        _finished?.Invoke(result);
        try { Close(); } catch { /* already closing */ }
    }
}
