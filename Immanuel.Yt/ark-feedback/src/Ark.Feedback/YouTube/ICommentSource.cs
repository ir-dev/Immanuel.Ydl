namespace Ark.Feedback.YouTube;

public sealed record FetchRequest(VideoUrl Url, int MaxComments, int RepliesPerThread, string Sort = "top");

/// <summary>Where a video's metadata and comments come from (yt-dlp, or the YouTube Data API when a key is configured).</summary>
public interface ICommentSource
{
    string Name { get; }
    bool Available { get; }
    string? Unavailability { get; }
    Task<CommentFetchResult> FetchAsync(FetchRequest request, IProgress<FetchProgress>? progress, CancellationToken ct);
}
