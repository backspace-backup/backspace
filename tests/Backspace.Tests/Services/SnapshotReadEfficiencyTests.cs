using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class SnapshotReadEfficiencyTests
{
    private static readonly string Snapshot = new('a', 64);

    [Fact]
    public async Task KnownImmutableSelectionAvoidsAnotherRepositoryList_ButPositionsAndUnknownIdsDoNot()
    {
        using var fixture = new Fixture();
        await fixture.List();
        Assert.Equal(Snapshot, (await fixture.Resolve(Snapshot)).SnapshotId);
        Assert.Equal(Snapshot, (await fixture.Resolve(Snapshot)).SnapshotId);
        Assert.Equal(1, fixture.Handler.ListCount);
        await fixture.Resolve(null);
        Assert.Equal(2, fixture.Handler.ListCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Resolve(new string('b', 64)));
        Assert.Equal(3, fixture.Handler.ListCount);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("uri")]
    [InlineData("guid")]
    [InlineData("plan")]
    [InlineData("source")]
    public async Task ChangedConfigurationOrSelectionCannotReuseMembership(string change)
    {
        using var fixture = new Fixture();
        await fixture.List();
        switch (change)
        {
            case "revision": fixture.Handler.Config.Modno++; break;
            case "uri": fixture.Handler.Config.Repos[0].Uri = "another-repository"; break;
            case "guid": fixture.Handler.Config.Repos[0].Guid = new string('e', 64); break;
            case "plan": fixture.Backup.Id = "another-backup"; break;
            case "source": fixture.Source = "another-source"; break;
        }
        fixture.Handler.HasSnapshot = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Resolve(Snapshot));
        Assert.Equal(2, fixture.Handler.ListCount);
    }

    [Fact]
    public async Task ARefreshRemovingASnapshotInvalidatesItsEarlierMembership()
    {
        using var fixture = new Fixture();
        await fixture.List();
        fixture.Handler.HasSnapshot = false;
        Assert.Empty(await fixture.List());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Resolve(Snapshot));
    }

    [Fact]
    public async Task ConfigurationChangeDuringListingDoesNotSeedTheCache()
    {
        using var fixture = new Fixture();
        fixture.Handler.ChangeDuringList = true;
        await fixture.List();
        fixture.Handler.HasSnapshot = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Resolve(Snapshot));
    }

    [Fact]
    public void BulkTreeKeepsNestedUnicodeAndLiteralBracketNames_ExcludesSiblingTrees()
    {
        var listing = Tree();
        listing.Accept(Node("/C/source", "dir"));
        listing.Accept(Node("/C/source/nested", "dir"));
        listing.Accept(Node("/C/source/nested/café日本語[1].txt", "file", 42));
        listing.Accept(Node("/C/source/empty.bin", "file", 0));
        listing.Accept(Node("/C/source-old/secret", "file", 12));
        listing.Accept(Node("/C/Source/secret", "file", 12));
        Assert.True(listing.IsComplete);
        Assert.Equal(new[] { "", "nested", "nested/café日本語[1].txt", "empty.bin" }, listing.Nodes.Select(n => n.Path));
        Assert.Equal(42, listing.Nodes[2].SizeBytes);
        Assert.Equal(0, listing.Nodes[3].SizeBytes);
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("{\"struct_type\":\"node\",\"type\":\"file\"}")]
    [InlineData("{\"struct_type\":\"node\",\"path\":\"/C/source/file\",\"type\":\"file\",\"size\":-1}")]
    [InlineData("{\"struct_type\":\"node\",\"path\":\"/C/source/../outside\",\"type\":\"file\"}")]
    [InlineData("{\"struct_type\":\"snapshot\",\"id\":\"another\"}")]
    public void InvalidOutputCannotPublishAPartialTree(string invalid)
    {
        var listing = Tree();
        listing.Accept(Node("/C/source/valid.txt", "file", 10));
        listing.Accept(invalid);
        Assert.False(listing.IsComplete);
    }

    [Fact]
    public void MissingOrWrongSnapshotHeaderCannotPublishATree()
    {
        var missing = new ResticBackrestEngine.SnapshotTreeListing("/C/source", Snapshot);
        missing.Accept(Node("/C/source/valid.txt", "file"));
        Assert.False(missing.IsComplete);
        var wrong = new ResticBackrestEngine.SnapshotTreeListing("/C/source", Snapshot);
        wrong.Accept("{\"struct_type\":\"snapshot\",\"id\":\"wrong\"}");
        Assert.False(wrong.IsComplete);
        Assert.True(Tree().IsComplete); // An actual empty snapshot is valid.
    }

    private static string Node(string path, string type, long size = 0) => JsonSerializer.Serialize(new { struct_type = "node", path, type, size });

    [Theory]
    [InlineData("C:\\")] [InlineData("C:/")]
    public void WholeDriveListingKeepsRootFilesAndExcludesOtherDrives(string source)
    {
        var prefix = ResticBackrestEngine.SourcePathToSnapshotPrefix(source);
        Assert.Equal("C", prefix);
        var listing = new ResticBackrestEngine.SnapshotTreeListing("/" + prefix, Snapshot);
        listing.Accept(JsonSerializer.Serialize(new { struct_type = "snapshot", id = Snapshot }));
        listing.Accept(Node("/C", "dir"));
        listing.Accept(Node("/C/root.txt", "file", 10));
        listing.Accept(Node("/C/nested/file.txt", "file", 20));
        listing.Accept(Node("/C-old/wrong.txt", "file", 30));
        listing.Accept(Node("/D/wrong.txt", "file", 40));
        Assert.True(listing.IsComplete);
        Assert.Equal(new[] { "", "root.txt", "nested/file.txt" }, listing.Nodes.Select(n => n.Path));
    }
    private static ResticBackrestEngine.SnapshotTreeListing Tree()
    {
        var listing = new ResticBackrestEngine.SnapshotTreeListing("/C/source", Snapshot);
        listing.Accept(JsonSerializer.Serialize(new { struct_type = "snapshot", id = Snapshot }));
        return listing;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler = new();
        internal readonly Backup Backup = new() { Id = "backup" };
        internal string Source = "source";
        private readonly Destination _destination = new() { Id = "read-test" };
        private readonly HttpClient _http;
        private readonly ResticBackrestEngine _engine;
        internal Fixture()
        {
            _http = new(Handler) { BaseAddress = new("http://127.0.0.1:1/") };
            var sidecar = new BackrestSidecar("unused", "unused", "unused");
            typeof(BackrestSidecar).GetField("_proc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidecar, Process.GetCurrentProcess());
            typeof(BackrestSidecar).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidecar, new BackrestClient(_http));
            _engine = new(sidecar, new SecretsStore());
        }
        internal Task<System.Collections.Generic.IReadOnlyList<RevisionSummary>> List() =>
            _engine.ListSnapshotsAsync(Backup, Source, _destination, "default", TestContext.Current.CancellationToken);
        internal Task<(string SnapshotId, string RepoGuid)> Resolve(string? id) =>
            _engine.ResolveSnapshotIdAsync(Backup, Source, _destination, "default", 1, TestContext.Current.CancellationToken, id);
        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly BackrestConfig Config = new() { Modno = 1,
            Repos = { new() { Id = "read-test-default", Guid = new string('d', 64), Uri = "repository" } } };
        internal int ListCount;
        internal bool HasSnapshot = true;
        internal bool ChangeDuringList;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object response;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1.Backrest/GetConfig": response = Config; break;
                case "/v1.Backrest/ListSnapshots":
                    ListCount++;
                    if (ChangeDuringList) Config.Modno++;
                    response = new ResticSnapshotList { Snapshots = HasSnapshot ? new() { new() { Id = Snapshot, UnixTimeMs = 1 } } : new() };
                    break;
                default: throw new InvalidOperationException("Unexpected request: " + request.RequestUri.AbsolutePath);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
