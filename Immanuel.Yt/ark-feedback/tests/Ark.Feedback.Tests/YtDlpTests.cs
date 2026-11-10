using System.Text.Json;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Ark.Feedback.YouTube;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Tests;

public class YtDlpTests
{
    [Fact]
    public void ParsesARealInfoDict()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Files.TestData("ytdlp-info.json")));
        var v = YtDlpCommentSource.ParseVideo(doc.RootElement, "x");
        Assert.Equal("jNQXAC9IVRw", v.Id);
        Assert.Equal("Me at the zoo", v.Title);
        Assert.Equal("jawed", v.Channel);
        Assert.Equal(19, v.DurationSeconds);
        Assert.Equal("Film & Animation", v.Category);
        Assert.Equal(2005, v.UploadedAt!.Value.Year);
        Assert.True(v.ChannelVerified);
        var comments = YtDlpCommentSource.ParseComments(doc.RootElement);
        Assert.Equal(6, comments.Count);
        Assert.Null(comments[0].ParentId);
        Assert.True(comments[0].IsPinned);
        Assert.True(comments[0].IsFavorited);
        Assert.Equal(4_800_000, comments[0].LikeCount);
        var reply = comments.First(c => c.IsReply);
        Assert.Equal(comments[0].Id, reply.ParentId); // replies to replies are folded under the thread root
        Assert.NotNull(reply.PublishedAt);
    }

    [Fact]
    public void BuildsSafeArguments()
    {
        var opt = Microsoft.Extensions.Options.Options.Create(new FeedbackOptions { YouTube = { ExtraExtractorArgs = "player_client=web" } });
        var paths = new FeedbackPaths(opt.Value, Directory.CreateTempSubdirectory("ark-feedback-tests-").FullName);
        var tools = new ToolLocator(opt, paths, new HttpDownloader(new NoHttp(), Logs.Of<HttpDownloader>()), Logs.Of<ToolLocator>());
        var src = new YtDlpCommentSource(tools, opt, Logs.Of<YtDlpCommentSource>());
        VideoUrl.TryParse("https://youtu.be/jNQXAC9IVRw", out var url, out _);
        var args = src.BuildArgs(new FetchRequest(url!, 600, 3));
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", args[^1]);
        Assert.Contains("--dump-single-json", args);
        Assert.Contains("--write-comments", args);
        var ex = args[args.ToList().IndexOf("--extractor-args") + 1];
        Assert.Contains("max_comments=600,all,300,3", ex);
        Assert.Contains("player_client=web", ex);
        Assert.DoesNotContain(args, a => a.Contains(' ') && a.StartsWith("http", StringComparison.Ordinal)); // one argument per value, never a shell string
    }

    [Fact]
    public void TranslatesYtDlpErrorsHonestly()
    {
        var botCheck = "WARNING: [youtube] jNQXAC9IVRw: Unable to download webpage: HTTP Error 429: Too Many Requests\nWARNING: [youtube] No title found\nERROR: [youtube] jNQXAC9IVRw: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.\nThe page needs to be reloaded.";
        var ex = YtDlpCommentSource.Translate(botCheck);
        Assert.Contains("rate-limiting", ex.Message);
        Assert.True(ex.Retryable);
        Assert.StartsWith("Sign in to confirm", ex.Detail);

        ex = YtDlpCommentSource.Translate("ERROR: [youtube] abcdefghijk: Private video. Sign in if you've been granted access to this video");
        Assert.Contains("private", ex.Message);
        Assert.False(ex.Retryable);

        ex = YtDlpCommentSource.Translate("ERROR: [youtube] abcdefghijk: Video unavailable. This video has been removed by the uploader");
        Assert.Contains("isn't available", ex.Message);

        ex = YtDlpCommentSource.Translate("ERROR: [youtube] abcdefghijk: Sign in to confirm your age. This video may be inappropriate for some users.");
        Assert.Contains("age-restricted", ex.Message);

        ex = YtDlpCommentSource.Translate("[debug] something\nWARNING: odd");
        Assert.True(ex.Retryable);
        Assert.Equal("WARNING: odd", ex.Detail);
    }

    [Fact]
    public void SelectorPrefersTheApiOnlyWithAKey()
    {
        var noKey = Microsoft.Extensions.Options.Options.Create(new FeedbackOptions());
        var paths = new FeedbackPaths(noKey.Value, Directory.CreateTempSubdirectory("ark-feedback-tests-").FullName);
        var tools = new ToolLocator(noKey, paths, new HttpDownloader(new NoHttp(), Logs.Of<HttpDownloader>()), Logs.Of<ToolLocator>());
        var yt = new YtDlpCommentSource(tools, noKey, Logs.Of<YtDlpCommentSource>());
        var api = new DataApiCommentSource(new NoHttp(), noKey, Logs.Of<DataApiCommentSource>());
        Assert.Same(yt, new CommentSourceSelector(api, yt, noKey).Primary);
        var withKey = Microsoft.Extensions.Options.Options.Create(new FeedbackOptions { YouTube = { ApiKey = "k" } });
        var api2 = new DataApiCommentSource(new NoHttp(), withKey, Logs.Of<DataApiCommentSource>());
        Assert.Same(api2, new CommentSourceSelector(api2, yt, withKey).Primary);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
