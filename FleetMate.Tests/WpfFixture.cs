using System.Windows;
using System.Windows.Threading;
using Xunit;
using Xunit.Sdk;

namespace FleetMate.Tests;

/// <summary>
/// Every test that builds a WPF object, or runs code that reads
/// <see cref="Application.Current"/>, belongs to this one collection. Its
/// tests run one at a time, after every parallel collection has finished, on
/// the single UI thread <see cref="WpfFixture"/> owns.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "WPF";
}

/// <summary>
/// One STA thread running a dispatcher for the whole test run, with the one
/// <see cref="Application"/> and the shared resources the views look up.
/// </summary>
/// <remarks>
/// A WPF <see cref="Application"/> is process-wide and bound to the
/// dispatcher of the thread that created it. Tests that each started their
/// own STA thread raced to create it, and work posted to its dispatcher never
/// ran once that thread had ended or blocked — the test host then waited
/// forever. Here there is one thread for every test body, every wait is
/// bounded, and a body that overruns fails with a message instead of hanging.
/// </remarks>
public sealed class WpfFixture : IDisposable
{
    /// <summary>How long one test body may run on the UI thread.</summary>
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long one <see cref="Pump"/> may wait for its task.</summary>
    public static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<Dispatcher> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object Gate = new();
    private static Task? _running;
    private static string? _stuck;
    private static Exception? _unhandled;

    public WpfFixture() => _ = Shared.Value;

    /// <summary>The UI thread's dispatcher, started on first use.</summary>
    public static Dispatcher Dispatcher => Shared.Value;

    /// <summary>Runs <paramref name="body"/> on the UI thread and waits for it, at most <see cref="BodyTimeout"/>.</summary>
    public static void Run(Action body) => Run<object?>(() => { body(); return null; });

    /// <summary>Runs <paramref name="body"/> on the UI thread and returns its result, waiting at most <see cref="BodyTimeout"/>.</summary>
    public static T Run<T>(Func<T> body)
    {
        var task = Post(() => Task.FromResult<object?>(body()));
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(BodyTimeout)) throw Overran();
        return Unwrap<T>(task);
    }

    /// <summary>Runs the asynchronous <paramref name="body"/> on the UI thread, waiting at most <see cref="BodyTimeout"/>.</summary>
    public static async Task RunAsync(Func<Task> body)
    {
        var task = Post(async () => { await body(); return (object?)null; });
        try { await task.WaitAsync(BodyTimeout); }
        catch (TimeoutException) { throw Overran(); }
        Unwrap(task);
    }

    /// <summary>
    /// Runs the dispatcher until <paramref name="task"/> finishes, so the
    /// continuations it posts back to this thread run; fails after
    /// <paramref name="timeout"/> (default <see cref="PumpTimeout"/>) instead of waiting forever.
    /// </summary>
    public static void Pump(Task task, TimeSpan? timeout = null)
    {
        var limit = timeout ?? PumpTimeout;
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var expiry = new CancellationTokenSource(limit);
            using var expired = expiry.Token.Register(() => frame.Continue = false);
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        if (!task.IsCompleted)
            throw new XunitException($"The awaited work did not finish within {limit.TotalSeconds:0} s on the UI thread.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets queued dispatcher work run until <paramref name="done"/> holds; fails after <paramref name="milliseconds"/>.</summary>
    public static void PumpUntil(Func<bool> done, string what, int milliseconds = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!done() && DateTime.UtcNow < deadline) Pump(Task.Delay(25));
        Assert.True(done(), what);
    }

    public void Dispose()
    {
        if (!Shared.IsValueCreated) return;
        var dispatcher = Shared.Value;
        if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
        dispatcher.Thread.Join(TimeSpan.FromSeconds(10));
    }

    private static Task<object?> Post(Func<Task<object?>> body)
    {
        var test = TestName();
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            // A body that overran still holds the thread: fail fast rather
            // than queue behind it and time out once more per test.
            if (_running is { IsCompleted: false })
                throw new XunitException($"The WPF test thread is still blocked by {_stuck ?? "an earlier test"}, which overran its {BodyTimeout.TotalSeconds:0} s limit.");
            _stuck = test;
            _running = done.Task;
        }
        Dispatcher.BeginInvoke(async () =>
        {
            _unhandled = null;
            try
            {
                var result = await body();
                if (_unhandled is { } stray) done.TrySetException(stray);
                else done.TrySetResult(result);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    private static T Unwrap<T>(Task<object?> task) => (T)task.GetAwaiter().GetResult()!;

    private static void Unwrap(Task<object?> task) => _ = task.GetAwaiter().GetResult();

    private static XunitException Overran() =>
        new($"The test did not finish within {BodyTimeout.TotalSeconds:0} s on the WPF test thread. Something it waits for never ran: " +
            "look for a dispatcher wait or a blocking .Result in the test or the code it drives.");

    private static string TestName() =>
        new System.Diagnostics.StackTrace().GetFrames()
            .Select(f => f.GetMethod())
            .FirstOrDefault(m => m?.DeclaringType is { } t && t.Namespace == typeof(WpfFixture).Namespace && t != typeof(WpfFixture)
                                 && !t.Name.Contains('<'))
            is { } method ? $"{method.DeclaringType!.Name}.{method.Name}" : "an earlier test";

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                // A failure in posted work fails the test that is running
                // rather than taking the test host down.
                dispatcher.UnhandledException += (_, e) => { _unhandled ??= e.Exception; e.Handled = true; };
                PrepareResources();
            }
            catch (Exception ex) { failure = ex; }
            finally { ready.Set(); }
            if (failure == null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(60)))
            throw new XunitException("The WPF test thread did not start within 60 s.");
        if (failure != null) throw new XunitException("The WPF test thread could not prepare its resources: " + failure);
        return dispatcher!;
    }

    /// <summary>
    /// The app's theme and the keys the views look up, without the app
    /// itself: an App instance would start FleetMate. Plain stand-in styles
    /// are enough for the views to build and lay out.
    /// </summary>
    private static void PrepareResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = app.Resources;
        resources.MergedDictionaries.Add(new ModernWpf.ThemeResources());
        resources.MergedDictionaries.Add(new ModernWpf.Controls.XamlControlsResources());
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/fleetmate-gui;component/Views/Shared/DiffTemplates.xaml"),
        });
        resources["CardStyle"] = new Style(typeof(System.Windows.Controls.Border));
        resources["NavigationTabStyle"] = new Style(typeof(System.Windows.Controls.RadioButton));
        resources["FleetListViewItemStyle"] = new Style(typeof(System.Windows.Controls.ListViewItem));
        resources["SectionHeaderStyle"] = new Style(typeof(System.Windows.Controls.TextBlock));
        resources["SubtitleTextStyle"] = new Style(typeof(System.Windows.Controls.TextBlock));
        resources["AppBackgroundBrush"] = System.Windows.Media.Brushes.White;
        resources["SubtleFillBrush"] = System.Windows.Media.Brushes.LightGray;
        resources["CardBorderBrush"] = System.Windows.Media.Brushes.Gray;
    }
}
