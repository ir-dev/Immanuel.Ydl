using Ark.Feedback.YouTube;

namespace Ark.Feedback.Tests;

public class VideoUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "jNQXAC9IVRw", "watch")]
    [InlineData("https://youtu.be/jNQXAC9IVRw?t=10", "jNQXAC9IVRw", "short-link")]
    [InlineData("youtube.com/shorts/jNQXAC9IVRw", "jNQXAC9IVRw", "shorts")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=jNQXAC9IVRw", "jNQXAC9IVRw", "watch")]
    [InlineData("https://music.youtube.com/watch?v=jNQXAC9IVRw&list=RD1", "jNQXAC9IVRw", "watch")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw", "jNQXAC9IVRw", "live")]
    [InlineData("https://www.youtube-nocookie.com/embed/jNQXAC9IVRw", "jNQXAC9IVRw", "embed")]
    [InlineData("jNQXAC9IVRw", "jNQXAC9IVRw", "id")]
    public void ParsesVideoLinks(string input, string id, string kind)
    {
        Assert.True(VideoUrl.TryParse(input, out var url, out var problem), problem);
        Assert.Equal(id, url!.VideoId);
        Assert.Equal(kind, url.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=" + id, url.Canonical);
    }

    [Theory]
    [InlineData("", "Paste a YouTube link first.")]
    [InlineData("https://vimeo.com/123", "Only YouTube")]
    [InlineData("https://www.youtube.com/playlist?list=PL123", "playlist")]
    [InlineData("https://www.youtube.com/@jawed", "channel")]
    [InlineData("https://www.youtube.com/watch?v=short", "Couldn't find a video id")]
    [InlineData("not a link at all", "")]
    public void RefusesWhatIsNotOneVideo(string input, string expectedProblem)
    {
        Assert.False(VideoUrl.TryParse(input, out _, out var problem));
        Assert.Contains(expectedProblem, problem, StringComparison.OrdinalIgnoreCase);
    }
}
