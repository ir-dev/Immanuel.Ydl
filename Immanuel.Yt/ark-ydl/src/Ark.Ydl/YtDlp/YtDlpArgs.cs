using System.Text;
using Ark.Ydl.Options;
using Ark.Ydl.Util;

namespace Ark.Ydl.YtDlp;

/// <summary>
/// Builds yt-dlp argument lists. Pure functions: everything here is covered by unit tests,
/// and nothing a user sends reaches the list except through a whitelist or a number.
/// </summary>
public static class YtDlpArgs
{
    public const string ProgressPrefix = "@@P|";
    public const string PostProgressPrefix = "@@PP|";

    /// <summary>Machine-readable progress lines parsed by <see cref="ProgressTracker"/>.</summary>
    public const string ProgressTemplate =
        "download:" + ProgressPrefix +
        "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s" +
        "|%(progress.speed)s|%(progress.eta)s|%(info.playlist_index)s|%(info.id)s";

    public const string PostProgressTemplate =
        "postprocess:" + PostProgressPrefix + "%(progress.status)s|%(progress.postprocessor)s|%(info.id)s";

    /// <summary>Cover art cropped to a centred square, as music players expect.</summary>
    public const string SquareCoverPpa =
        "ThumbnailsConvertor:-qmin 1 -q:v 1 -vf crop=\"'if(gt(ih,iw),iw,ih)':'if(gt(iw,ih),ih,iw)'\"";

    /// <summary>Arguments every call shares: no user config, no colours, our cache, tools, cookies, proxy.</summary>
    public static List<string> Common(YdlOptions o, string cacheDir, IReadOnlyList<string> toolArgs, string? cookiesCopy)
    {
        var a = new List<string> { "--ignore-config", "--color", "never", "--cache-dir", cacheDir };
        a.AddRange(toolArgs);
        if (cookiesCopy is not null) a.AddRange(["--cookies", cookiesCopy]);
        if (!string.IsNullOrWhiteSpace(o.Proxy)) a.AddRange(["--proxy", o.Proxy]);
        a.AddRange(o.ExtraArgs.Where(x => !string.IsNullOrWhiteSpace(x)));
        return a;
    }

    public static List<string> Probe(IReadOnlyList<string> common, UrlAnalysis url, bool playlist, int maxItems)
    {
        var a = new List<string>(common) { "-J" };
        if (playlist) a.AddRange(["--yes-playlist", "--flat-playlist", "-I", $"1:{Math.Max(1, maxItems)}"]);
        else a.Add("--no-playlist");
        a.AddRange(["--", url.Url]);
        return a;
    }

    public static bool IsPlaylist(DownloadRequest r, UrlAnalysis url) =>
        r.Playlist && url.Kind is UrlKind.Playlist or UrlKind.Channel or UrlKind.Other;

    public static List<string> Download(DownloadRequest r, UrlAnalysis url, IReadOnlyList<string> common, string jobDir, YdlOptions o)
    {
        var a = new List<string>(common);
        a.AddRange([
            "--newline", "--progress", "--progress-delta", "0.5",
            "--progress-template", ProgressTemplate,
            "--progress-template", PostProgressTemplate,
            "--no-mtime", "--windows-filenames", "--trim-filenames", "180",
            "--concurrent-fragments", Math.Clamp(o.ConcurrentFragments, 1, 16).ToString(),
            "-P", jobDir,
            "-P", "temp:" + Path.Combine(jobDir, ".tmp"),
        ]);
        if (!string.IsNullOrWhiteSpace(o.MaxFileSize)) a.AddRange(["--max-filesize", o.MaxFileSize.Trim()]);

        var playlist = IsPlaylist(r, url);
        if (playlist)
        {
            a.AddRange(["--yes-playlist", "--ignore-errors"]);
            a.AddRange(["-I", r.PlaylistItems is { Count: > 0 } items
                ? DownloadRequestValidator.CompactItems(items.Take(o.MaxPlaylistItems))
                : $"1:{Math.Max(1, o.MaxPlaylistItems)}"]);
        }
        else
        {
            a.Add("--no-playlist");
        }

        var clip = r.Mode is DownloadMode.Video or DownloadMode.Audio && !playlist ? r.Clip : null;
        var wholeMedia = clip is null && r.Mode is DownloadMode.Video or DownloadMode.Audio;

        // Never record a live stream forever; optionally cap whole-video length.
        var filter = "!is_live";
        if (wholeMedia && o.MaxDurationMinutes > 0) filter += $" & duration <=? {o.MaxDurationMinutes * 60}";
        a.AddRange(["--match-filter", filter]);

        switch (r.Mode)
        {
            case DownloadMode.Video:
                AddVideo(a, r);
                break;
            case DownloadMode.Audio:
                AddAudio(a, r, url, playlist);
                break;
            case DownloadMode.Subtitles:
                a.AddRange(["--skip-download", "--write-subs"]);
                if (r.Subtitles.AutoLangs.Count > 0) a.Add("--write-auto-subs");
                a.AddRange(["--sub-langs", SubLangs(r.Subtitles), "--convert-subs", "srt"]);
                break;
            case DownloadMode.Thumbnail:
                a.AddRange(["--skip-download", "--write-thumbnail", "--convert-thumbnails", "jpg"]);
                break;
        }

        if (r.Mode is DownloadMode.Video or DownloadMode.Audio)
        {
            if (r.Embed.Metadata) a.Add("--embed-metadata");
            a.Add(r.Embed.Chapters ? "--embed-chapters" : "--no-embed-chapters");

            if (wholeMedia && r.SponsorBlock.Action != SponsorAction.None && r.SponsorBlock.Categories.Count > 0)
                a.AddRange([
                    r.SponsorBlock.Action == SponsorAction.Remove ? "--sponsorblock-remove" : "--sponsorblock-mark",
                    string.Join(',', r.SponsorBlock.Categories.Distinct()),
                ]);
        }

        var name = BaseName(r.FileName);
        if (playlist && r.NumberFiles) name = "%(playlist_index)03d - " + name;

        if (clip is { SplitChapters: true })
        {
            a.Add("--split-chapters");
            a.AddRange(["-o", ".full/" + name + ".%(ext)s"]);
            a.AddRange(["-o", "chapter:" + name + " - %(section_number)02d %(section_title).80B.%(ext)s"]);
        }
        else if (clip is not null)
        {
            foreach (var s in clip.Sections)
                a.AddRange(["--download-sections", SectionSpec(s)]);
            if (clip.Precise) a.Add("--force-keyframes-at-cuts");
            var byChapter = clip.Sections.All(s => !string.IsNullOrEmpty(s.Chapter));
            name += byChapter
                ? " - %(section_title).80B"
                : " [%(section_start>%H-%M-%S)s-%(section_end>%H-%M-%S)s]";
            a.AddRange(["-o", name + ".%(ext)s"]);
        }
        else
        {
            a.AddRange(["-o", name + ".%(ext)s"]);
        }

        a.AddRange(["--", url.Url]);
        return a;
    }

    private static void AddVideo(List<string> a, DownloadRequest r)
    {
        var v = r.Video;
        var codec = v.Codec;
        if (v.Container == VideoContainer.Webm && codec is VideoCodec.Auto or VideoCodec.H264)
            codec = codec == VideoCodec.H264 ? VideoCodec.Vp9 : VideoCodec.Best; // WebM can't carry H.264/AAC

        var res = v.MaxHeight is { } h ? $"res:{h}" : "res";
        var sort = new List<string>();
        switch (codec)
        {
            case VideoCodec.Auto: sort.AddRange([res, "fps"]); AddHdr(); sort.AddRange(["vcodec:h264", "acodec:m4a"]); break;
            case VideoCodec.Best: sort.AddRange([res, "fps"]); AddHdr(); break;
            case VideoCodec.H264: sort.AddRange(["vcodec:h264", res, "fps", "acodec:m4a"]); break;
            case VideoCodec.Vp9: sort.AddRange(["vcodec:vp9", res, "fps"]); AddHdr(); break;
            case VideoCodec.Av1: sort.AddRange(["vcodec:av01", res, "fps"]); AddHdr(); break;
        }
        if (v.Container == VideoContainer.Webm) sort.Add("acodec:opus");

        a.AddRange(["-f", v.Container == VideoContainer.Webm ? "bv*[ext=webm]+ba[ext=webm]/b[ext=webm]/bv*+ba/b" : "bv*+ba/b"]);
        a.AddRange(["-S", string.Join(',', sort)]);

        switch (v.Container)
        {
            case VideoContainer.Mp4: a.AddRange(["--merge-output-format", "mp4", "--remux-video", "mp4"]); break;
            case VideoContainer.Mkv: a.AddRange(["--merge-output-format", "mkv", "--remux-video", "mkv"]); break;
            case VideoContainer.Webm: a.AddRange(["--merge-output-format", "webm", "--remux-video", "webm>webm/mkv"]); break;
        }

        // WebM has no cover-art slot; asking for one fails the post-processor.
        if (r.Embed.Thumbnail && v.Container != VideoContainer.Webm) a.Add("--embed-thumbnail");

        var subs = r.Subtitles;
        if (subs.Mode != SubtitleMode.None && subs.Langs.Count + subs.AutoLangs.Count > 0)
        {
            a.Add("--write-subs");
            if (subs.AutoLangs.Count > 0) a.Add("--write-auto-subs");
            a.AddRange(["--sub-langs", SubLangs(subs)]);
            if (subs.Mode is SubtitleMode.Embed or SubtitleMode.Both) a.Add("--embed-subs");
            if (subs.Mode is SubtitleMode.File or SubtitleMode.Both) a.AddRange(["--convert-subs", "srt"]);
        }
        return;

        void AddHdr()
        {
            if (!v.Hdr) sort.Add("+hdr"); // reversed: SDR first
        }
    }

    private static void AddAudio(List<string> a, DownloadRequest r, UrlAnalysis url, bool playlist)
    {
        var f = r.Audio.Format;
        a.AddRange(["-f", f switch
        {
            AudioFormat.M4a => "ba[ext=m4a]/ba/b",
            AudioFormat.Opus => "ba[acodec^=opus]/ba/b",
            _ => "ba/b",
        }]);
        a.AddRange(["-x", "--audio-format", f switch
        {
            AudioFormat.Mp3 => "mp3",
            AudioFormat.M4a => "m4a",
            AudioFormat.Opus => "opus",
            AudioFormat.Flac => "flac",
            AudioFormat.Wav => "wav",
            AudioFormat.Ogg => "vorbis",
            _ => "best",
        }]);
        if (f is AudioFormat.Mp3 or AudioFormat.M4a or AudioFormat.Opus or AudioFormat.Ogg)
            a.AddRange(["--audio-quality", r.Audio.Bitrate is > 0 and var b ? $"{b}K" : "0"]);

        if (r.Embed.Thumbnail && f != AudioFormat.Wav)
        {
            a.Add("--embed-thumbnail");
            if (r.Audio.SquareCover) a.AddRange(["--convert-thumbnails", "jpg", "--ppa", SquareCoverPpa]);
        }

        if (r.Audio.SmartTags && r.Embed.Metadata && !url.IsMusic)
        {
            a.AddRange(["--parse-metadata", "title:%(artist)s - %(title)s"]);
            if (playlist)
                a.AddRange([
                    "--parse-metadata", "playlist_title:%(album)s",
                    "--parse-metadata", "playlist_index:%(track_number)s",
                ]);
        }

        var subs = r.Subtitles;
        if (subs.Mode is SubtitleMode.File or SubtitleMode.Both && subs.Langs.Count + subs.AutoLangs.Count > 0)
        {
            a.Add("--write-subs");
            if (subs.AutoLangs.Count > 0) a.Add("--write-auto-subs");
            a.AddRange(["--sub-langs", SubLangs(subs), "--convert-subs", "srt"]);
        }
    }

    internal static string BaseName(FileNameStyle style) => style switch
    {
        FileNameStyle.TitleId => "%(title).150B [%(id)s]",
        FileNameStyle.ChannelTitle => "%(channel,uploader|Unknown).60B - %(title).140B",
        FileNameStyle.DateTitle => "%(upload_date>%Y-%m-%d)s - %(title).150B",
        _ => "%(title).170B",
    };

    internal static string SectionSpec(ClipSection s) =>
        !string.IsNullOrEmpty(s.Chapter)
            ? "^" + PyRegexEscape(s.Chapter) + "$"
            : $"*{TimeCode.ToArg(s.Start)}-{(s.End is { } e ? TimeCode.ToArg(e) : "inf")}";

    private static string SubLangs(SubtitleOptions s) =>
        string.Join(',', s.Langs.Concat(s.AutoLangs).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Escapes Python <c>re</c> metacharacters so a chapter title matches literally.</summary>
    internal static string PyRegexEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (@"\.^$*+?{}[]|()".Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
