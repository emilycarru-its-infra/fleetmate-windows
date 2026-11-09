using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Teams;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Teams;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public sealed class TeamsAccessPresentationTests : TempFolder
{
    private sealed class Api : HttpMessageHandler
    {
        public int? DirectoryDenial { get; set; }
        public bool MissingThread { get; set; }
        public int DirectoryReads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var directory = request.RequestUri!.AbsolutePath.EndsWith("/channels");
            if (directory) DirectoryReads++;
            var status = directory ? (HttpStatusCode)(DirectoryDenial ?? 200) : MissingThread ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            var body = directory ? """
                {"as_of":null,"channels":[{"team_id":"team","team_name":"Example Team","channel_id":"c1","channel_name":"General","membership_type":"standard","threads":1,"last_activity_at":null}]}
                """ : """
                {"thread_id":"t1","team_id":"team","team_name":"Example Team","channel_id":"c1","channel_name":"General","subject":"Topic","web_url":null,"messages":[{"message_id":"t1","reply_to_id":null,"author_display_name":"Example author","created_at":"2026-11-01T12:00:00Z","edited_at":null,"text":"Authorized message text","attachments":[],"web_url":null}]}
                """;
            if (status == HttpStatusCode.ServiceUnavailable) body = """{"detail":"teams_membership_unavailable"}""";
            if (status == HttpStatusCode.Forbidden) body = """{"detail":"access_denied"}""";
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (!(directory && DirectoryDenial == 200)) response.Headers.Add(TeamsSearchService.AccessHeader, TeamsSearchService.AccessVersion);
            return Task.FromResult(response);
        }
    }

    private TeamsPage Page(Api api, out TeamsSavedThreads store, out TeamsSearchService service)
    {
        store = new TeamsSavedThreads("https://search.example.org", "member@example.org", Path.Combine(Root, "state"));
        TeamsSearchHit Hit(string channel) => new("t1", "team", "Example Team", channel, "General", "Topic", "Summary", "Example author",
            "2026-11-01T12:00:00Z", "2026-11-01T12:00:00Z", 0, Array.Empty<string>(), 0, null);
        store.Save(Hit("c1"));
        store.Save(Hit("c2"));
        var tokens = new EntraTokenSource("00000000-0000-0000-0000-000000000000")
        {
            AcquireOverride = (_, _) => Task.FromResult(("fixture-token", DateTimeOffset.UtcNow.AddHours(1))),
        };
        service = new TeamsSearchService("https://search.example.org", "api://example/.default", api, () => tokens);
        return new TeamsPage(new FleetMateConfig { TeamsApiUrl = "https://search.example.org", TeamsApiScope = "api://example/.default" }, service, store);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(503)]
    [InlineData(200)]
    public Task SavedRowsWaitForAuthorizedDirectoryAndDenialClearsLoadedMessage(int denial) => WpfFixture.RunAsync(async () =>
    {
        var api = new Api();
        var page = Page(api, out var store, out var service);
        using (service)
        {
            var tree = Assert.IsType<TreeView>(page.FindName("ChannelTree"));
            ((TreeViewItem)tree.Items[0]).IsSelected = true;
            var results = Assert.IsType<ListBox>(page.FindName("ResultsList"));
            Assert.Empty(results.Items.Cast<object>());
            await page.LoadChannelsAsync();
            Assert.Single(results.Items.Cast<object>());
            results.SelectedIndex = 0;
            var cards = Assert.IsType<StackPanel>(page.FindName("ThreadCards"));
            WpfFixture.PumpUntil(() => cards.Children.OfType<ConversationRailRow>().Any(), "authorized thread content");
            Assert.True(Assert.IsType<Button>(page.FindName("CopyThreadButton")).IsEnabled);
            api.DirectoryDenial = denial;
            await page.LoadChannelsAsync();
            Assert.Empty(results.Items.Cast<object>());
            Assert.Empty(cards.Children.Cast<object>());
            Assert.False(Assert.IsType<Button>(page.FindName("CopyThreadButton")).IsEnabled);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<DockPanel>(page.FindName("ThreadPanel")).Visibility);
            Assert.StartsWith("Couldn't load channels.", Assert.IsType<TextBlock>(page.FindName("StatusText")).Text);
            Assert.Equal(2, store.Load().Count);
        }
    });

    [Fact]
    public Task RemovedThreadHidesStaleSavedReferenceAndReloadsDirectory() => WpfFixture.RunAsync(async () =>
    {
        var api = new Api { MissingThread = true };
        var page = Page(api, out var store, out var service);
        using (service)
        {
            var tree = Assert.IsType<TreeView>(page.FindName("ChannelTree"));
            ((TreeViewItem)tree.Items[0]).IsSelected = true;
            await page.LoadChannelsAsync();
            var results = Assert.IsType<ListBox>(page.FindName("ResultsList"));
            results.SelectedIndex = 0;
            var status = Assert.IsType<TextBlock>(page.FindName("StatusText"));
            WpfFixture.PumpUntil(() => status.Text.Contains("no longer available"), "removed thread recovery");
            Assert.True(api.DirectoryReads >= 2);
            Assert.Empty(results.Items.Cast<object>());
            Assert.Equal(2, store.Load().Count);
        }
    });
}
