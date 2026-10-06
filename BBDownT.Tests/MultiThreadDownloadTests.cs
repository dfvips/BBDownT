using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class MultiThreadDownloadTests
{
    [Fact]
    public async Task SameLengthOldTrackCannotReplaceTheNewResource()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "OLD!");
        var expectedClip = files.FilePath("00000_track.vclip");
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.Headers.Range?.ToString() ?? "size probe");
            var response = new HttpResponseMessage(request.Headers.Range is null
                ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("NEW!"u8.ToArray())
            };
            if (request.Headers.Range is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
            return response;
        }));

        var manifest = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/new-quality.mp4", destination, new(), client);

        Assert.Equal(new[] { "size probe", "bytes=0-3" }, requests);
        Assert.Equal(new[] { expectedClip }, manifest);
        Assert.Equal("OLD!", File.ReadAllText(destination));
        BBDownTDownloadUtil.MergeTrackClips(manifest, destination);
        Assert.Equal("NEW!", File.ReadAllText(destination));
        Assert.False(File.Exists(expectedClip));
    }

    [Fact]
    public async Task FailedReplacementPreservesThePreviouslyCompletedTrack()
    {
        using var files = new MediaTestDirectory();
        var destination = files.Write("track.mp4", "OLD!");
        files.FilePath("00000_track.vclip");
        using var client = new HttpClient(new Handler(request => request.Headers.Range is null
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) }
            : throw new HttpRequestException("new resource unavailable")));

        await Assert.ThrowsAnyAsync<Exception>(() => BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/new-quality.mp4", destination, new(), client));

        Assert.Equal("OLD!", File.ReadAllText(destination));
    }

    [Fact]
    public async Task SkipsCompletedClipsAndResumesUnfinishedOnes()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        files.Write("00000_track.vclip", "ABCD");
        var partial = files.Write("00001_track.vclip", "EF");
        await new DownloadResumeValidator("\"entity-v1\"", null).SaveAsync(partial + ".resume");
        var ranges = new List<string?>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            if (request.Headers.Range is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ABCDEFGH"u8.ToArray()) };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("GH"u8.ToArray())
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"entity-v1\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(6, 7, 8);
            return response;
        }));

        var manifest = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.mp4", destination, new(), client);

        BBDownTDownloadUtil.MergeTrackClips(manifest, destination);
        Assert.Equal(2, ranges.Count);
        Assert.Null(ranges[0]);
        Assert.Equal("bytes=6-7", ranges[1]);
        Assert.Equal("ABCDEFGH", File.ReadAllText(destination));
        Assert.False(File.Exists(partial + ".resume"));
    }

    [Fact]
    public async Task RetriesOnlyTheUnfinishedClipWhenRemoteEntityChanged()
    {
        using var files = new MediaTestDirectory();
        var destination = files.FilePath("track.mp4");
        var partial = files.Write("00000_track.vclip", "AB");
        await new DownloadResumeValidator("\"entity-v1\"", null).SaveAsync(partial + ".resume");
        var ranges = new List<string?>();
        using var client = new HttpClient(new Handler(request =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            if (request.Headers.Range is null || request.Headers.IfRange is not null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("NEW!"u8.ToArray()) };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("NEW!"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
            return response;
        }));

        var manifest = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(
            "https://cdn.test/track.mp4", destination, new(), client);

        BBDownTDownloadUtil.MergeTrackClips(manifest, destination);
        Assert.Equal(3, ranges.Count);
        Assert.Null(ranges[0]);
        Assert.Equal("bytes=2-3", ranges[1]);
        Assert.Equal("bytes=0-3", ranges[2]);
        Assert.Equal("NEW!", File.ReadAllText(destination));
        Assert.False(File.Exists(partial + ".resume"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
