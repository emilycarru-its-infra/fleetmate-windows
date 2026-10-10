using System.Text;
using System.Windows;
using System.Windows.Threading;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Development.Repos;
using Microsoft.Win32;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Repos workspace end to end without a window: the app's resources, the
/// sidebar, the git pane, the editor, Insights and Settings › Repositories
/// built and laid out against a throwaway repository and registry, driven
/// through the same model the views use. Preferences go to a throwaway
/// registry key, so the person's own choices are never touched.
/// </summary>
public class RepoWorkspaceGuiTests
{
    [Fact]
    public void Workspace_LoadsAChecksOutRepository_AndNothingDestructiveRunsWithoutConsent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets queued dispatcher work (fire-and-forget loads, layout) run until <paramref name="done"/> or a timeout.</summary>
    private static void PumpUntil(Func<bool> done, int milliseconds = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Pump(Task.Delay(25));
        }
        Assert.True(done(), "timed out waiting for the workspace");
    }

    private static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        PrepareResources();

        var key = $@"SOFTWARE\FleetMate.Tests.{Guid.NewGuid():N}";
        RepoWorkspacePreferences.KeyPathOverride = key;
        var parent = Path.Combine(Path.GetTempPath(), "fleetmate-gui-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "repo");
        try
        {
            Directory.CreateDirectory(root);
            var copy = new GitWorkingCopy(root);
            foreach (var args in new[]
            {
                new[] { "init", "-q", "-b", "main" }, new[] { "config", "user.email", "dev@example.com" },
                new[] { "config", "user.name", "Dev" }, new[] { "config", "commit.gpgsign", "false" },
                new[] { "config", "core.autocrlf", "false" },
                new[] { "remote", "add", "origin", "https://github.com/example-org/example-repo.git" },
            })
            {
                Pump(copy.GitAsync(args));
            }
            copy.WriteFile("README.md", "# Example\n"u8.ToArray());
            copy.WriteFile("src/app.cs", Encoding.UTF8.GetBytes("var x = 1;\n"));
            Pump(copy.CommitAsync("Initial", null, new HashSet<string>(), allowProtected: true));
            Pump(copy.SwitchBranchAsync("feature/gui"));
            copy.WriteFile("README.md", "# Example\nchanged\n"u8.ToArray());
            copy.WriteFile("new.txt", "fresh\n"u8.ToArray());

            var manager = new RepoManager(new RepoRegistryStore(Path.Combine(parent, "registry", "repos.json")));
            Pump(manager.LinkAsync(root));

            var model = new RepoWorkspaceModel(manager);
            var errors = new List<string>();
            model.ErrorReported += (title, message) => errors.Add($"{title}: {message}");
            var sidebar = new RepoSidebar();
            var view = new RepoWorkspaceView();
            sidebar.Attach(model);
            view.Attach(model);
            // A test cannot answer a dialog: every confirmation is declined.
            var asked = new List<string>();
            model.Git.Confirm = (title, _, _) => { asked.Add(title); return false; };

            model.ReloadRecords();
            Pump(model.RefreshStatusesAsync());
            PumpUntil(() => model.Git.Files.Count == 2 && model.Tree.Count > 0 && model.Git.LastRefreshedAt != null);
            Draw(sidebar);
            Draw(view);

            Assert.Equal("github:example-org/example-repo", model.SelectedId);
            Assert.Equal("feature/gui", model.Git.CurrentBranch);
            Assert.Equal(1, model.Git.Commits.Count);
            Assert.Contains(model.Statuses.Values, s => s.ChangedCount == 2);
            Assert.Contains("+changed", model.Git.DiffText);

            // Discard asks, and a declined confirmation leaves the file alone.
            model.Git.FileSelection = model.Git.Files.Where(f => f.RelativePath == "README.md").Select(f => f.Id).ToList();
            Pump(model.Git.DiscardSelectedAsync());
            Assert.Single(asked);
            Assert.Contains("changed", Encoding.UTF8.GetString(copy.ReadFile("README.md")));

            // Stage one file and commit it from the composer.
            Pump(model.Git.StageAsync(new[] { "README.md" }));
            model.Git.CommitSubject = "Change the readme";
            Pump(model.Git.CommitAsync());
            Assert.Empty(errors);
            Assert.Equal("Change the readme", model.Git.Commits[0].Subject);
            Assert.Single(model.Git.Files);

            // History, Files and the editor.
            model.Git.Panel = RepoPanel.History;
            Layout(view);
            model.Git.CommitSelection = model.Git.Commits[0].Sha;
            Pump(model.Git.SyncDiffAsync());
            Draw(view);
            Assert.Contains("Change the readme", model.Git.DiffText);
            Assert.Equal(2, model.Git.Graph.Count);

            model.Git.Panel = RepoPanel.Files;
            model.FileFilter = "app";
            Layout(view);
            model.Open("src/app.cs");
            Assert.NotNull(model.Document);
            model.Document!.Text = "var x = 2;\n";
            model.EditorTextChanged();
            Assert.True(model.IsDirty);
            model.Save();
            Assert.False(model.IsDirty);
            Assert.Equal("var x = 2;\n", Encoding.UTF8.GetString(copy.ReadFile("src/app.cs")));
            Pump(model.GrepAsync("x = 2"));
            Assert.Equal("src/app.cs", Assert.Single(model.GrepResults).Path);
            Layout(view);

            // Insights for this repository and for every tracked one.
            model.Git.Panel = RepoPanel.Insights;
            Layout(view, 4000);
            model.Insights.Scope = RepoInsightsModel.InsightsScope.Repository;
            Pump(model.Insights.LoadAsync(manager, model.Selected, model.Tracked));
            Assert.Equal(2, model.Insights.Report?.Total.Commits);
            view.Insights.Render();
            Draw(view.Insights, 4000);
            model.Insights.Scope = RepoInsightsModel.InsightsScope.All;
            Pump(model.Insights.LoadAsync(manager, model.Selected, model.Tracked));
            Assert.Equal(2, model.Insights.Summary?.Combined.Total.Commits);
            view.Insights.Render();
            Draw(view.Insights, 4000);

            // The protected branch is refused, with the refusal reported.
            Pump(model.Git.SwitchBranchAsync("main"));
            Pump(model.Git.StageAsync(new[] { "new.txt" }));
            model.Git.CommitSubject = "On main";
            Pump(model.Git.CommitAsync());
            Assert.Contains(errors, e => e.StartsWith("Commit refused"));

            // Settings › Repositories lists the linked checkout.
            var settings = new RepositoriesSettingsView(manager);
            Layout(settings);
            settings.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Layout(settings);
        }
        finally
        {
            RepoWorkspacePreferences.KeyPathOverride = null;
            try { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); } catch (Exception) { }
            try
            {
                foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(parent, true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// The app's theme and the keys the Repos views look up, without the app
    /// itself: an App instance would start FleetMate. Plain stand-in styles
    /// are enough for the views to build and lay out.
    /// </summary>
    private static void PrepareResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = app.Resources;
        if (resources.Contains("CardStyle")) return;
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

    /// <summary>Lays out and renders the element, so every OnRender and template runs.</summary>
    /// <remarks>
    /// Insights is drawn tall enough that its scroll bar never switches on:
    /// the theme's scroll-bar animation needs a live window to run in.
    /// </remarks>
    private static void Draw(FrameworkElement element, int height = 800)
    {
        Layout(element, height);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    private static void Layout(FrameworkElement element, int height = 800)
    {
        element.Measure(new Size(1200, height));
        element.Arrange(new Rect(0, 0, 1200, height));
        element.UpdateLayout();
    }
}
