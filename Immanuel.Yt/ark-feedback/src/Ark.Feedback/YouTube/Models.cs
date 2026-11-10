namespace Ark.Feedback.YouTube;

/// <summary>What the report says about the video itself.</summary>
public sealed record VideoInfo
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Channel { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelUrl { get; init; }
    public long? ChannelFollowers { get; init; }
    public bool? ChannelVerified { get; init; }
    public double? DurationSeconds { get; init; }
    public long? ViewCount { get; init; }
    public long? LikeCount { get; init; }
    /// <summary>YouTube's own (approximate) total; the number we fetched is on the report.</summary>
    public long? CommentCount { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public string? Category { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Language { get; init; }
    public string? LiveStatus { get; init; }
    public bool IsShort { get; init; }
    public int? AgeLimit { get; init; }
    public string? Thumbnail { get; init; }
    public string Url => "https://www.youtube.com/watch?v=" + Id;
}

/// <summary>One comment as fetched, before any scoring.</summary>
public sealed record RawComment
{
    public required string Id { get; init; }
    /// <summary>Null for a top-level comment, else the id of the comment this one answers.</summary>
    public string? ParentId { get; init; }
    public required string Text { get; init; }
    public string? Author { get; init; }
    public string? AuthorId { get; init; }
    public bool AuthorIsUploader { get; init; }
    public bool AuthorIsVerified { get; init; }
    public bool IsPinned { get; init; }
    /// <summary>Hearted by the creator.</summary>
    public bool IsFavorited { get; init; }
    public long LikeCount { get; init; }
    public int? ReplyCount { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public bool IsReply => ParentId is not null;
}

public sealed record CommentFetchResult(VideoInfo Video, IReadOnlyList<RawComment> Comments, string Source, string? ToolVersion, IReadOnlyList<string> Warnings);

/// <summary>Live progress of a fetch: how many comments so far and YouTube's total when known.</summary>
public sealed record FetchProgress(int Fetched, long? Total, string Stage);

public sealed class CommentSourceException(string message, string? detail = null, bool retryable = false) : Exception(message)
{
    public string? Detail { get; } = detail;
    public bool Retryable { get; } = retryable;
}
