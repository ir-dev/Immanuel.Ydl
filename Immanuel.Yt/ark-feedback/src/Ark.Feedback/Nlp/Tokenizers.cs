using System.Globalization;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace Ark.Feedback.Nlp;

/// <summary>Turns text into the model's input ids, special tokens included, cut to a maximum length.</summary>
public interface ITextTokenizer
{
    int PadId { get; }
    /// <summary>Whether the model takes a token_type_ids input (BERT family).</summary>
    IReadOnlyList<int> Encode(string text, int maxTokens);
}

/// <summary>
/// The BERT WordPiece tokenizer exactly as Hugging Face's fast tokenizer does it for the cased multilingual models
/// (BertNormalizer: clean text + CJK spacing, no lowercasing, no accent stripping; BertPreTokenizer: whitespace +
/// punctuation; WordPiece with "##" continuations, [UNK] for a word with any unknown piece or over 100 characters).
/// Written here because Microsoft.ML.Tokenizers' BertTokenizer diverges on Indic scripts and emoji.
/// </summary>
public sealed class WordPieceTokenizer : ITextTokenizer
{
    private const int MaxInputCharsPerWord = 100;
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unk, _cls, _sep;
    private readonly bool _lowerCase;

    public WordPieceTokenizer(IEnumerable<string> vocabLines, bool lowerCase = false)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var raw in vocabLines)
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 && i > 0 && !_vocab.ContainsKey("")) { /* a blank line is still an id */ }
            _vocab.TryAdd(line, i);
            i++;
        }
        _unk = Id("[UNK]");
        _cls = Id("[CLS]");
        _sep = Id("[SEP]");
        PadId = Id("[PAD]");
        _lowerCase = lowerCase;
    }

    public static WordPieceTokenizer FromFile(string vocabPath, bool lowerCase = false) => new(File.ReadLines(vocabPath), lowerCase);

    private int Id(string token) => _vocab.TryGetValue(token, out var id) ? id : throw new InvalidDataException($"vocab.txt has no {token} token.");

    public int PadId { get; }

    public int VocabSize => _vocab.Count;

    public IReadOnlyList<int> Encode(string text, int maxTokens)
    {
        var ids = new List<int>(Math.Min(maxTokens, 64)) { _cls };
        var budget = Math.Max(1, maxTokens - 2);
        foreach (var word in PreTokenize(text))
        {
            foreach (var id in WordPiece(word))
            {
                if (ids.Count - 1 >= budget) goto done;
                ids.Add(id);
            }
        }
        done:
        ids.Add(_sep);
        return ids;
    }

    /// <summary>Clean + CJK spacing + (optional lowercase/strip accents) + split on whitespace and punctuation.</summary>
    internal IEnumerable<string> PreTokenize(string text)
    {
        var cleaned = new StringBuilder(text.Length + 16);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp == 0 || cp == 0xFFFD) continue;
            var cat = Rune.GetUnicodeCategory(rune);
            if (IsWhitespace(rune, cat)) { cleaned.Append(' '); continue; }
            if (IsControl(cat)) continue;
            if (IsCjk(cp)) { cleaned.Append(' ').Append(rune.ToString()).Append(' '); continue; }
            cleaned.Append(rune.ToString());
        }
        var s = cleaned.ToString();
        if (_lowerCase) s = StripAccents(s.ToLowerInvariant());

        var word = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (IsWhitespace(rune, cat))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            }
            else if (IsPunctuation(rune, cat))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return rune.ToString();
            }
            else word.Append(rune.ToString());
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private IEnumerable<int> WordPiece(string word)
    {
        var runes = word.EnumerateRunes().ToArray();
        if (runes.Length > MaxInputCharsPerWord) { yield return _unk; yield break; }
        var pieces = new List<int>(4);
        var start = 0;
        while (start < runes.Length)
        {
            var end = runes.Length;
            var found = -1;
            while (start < end)
            {
                var sb = new StringBuilder();
                if (start > 0) sb.Append("##");
                for (var i = start; i < end; i++) sb.Append(runes[i].ToString());
                if (_vocab.TryGetValue(sb.ToString(), out var id)) { found = id; break; }
                end--;
            }
            if (found < 0) { yield return _unk; yield break; }
            pieces.Add(found);
            start = end;
        }
        foreach (var p in pieces) yield return p;
    }

    private static bool IsWhitespace(Rune r, UnicodeCategory cat) =>
        r.Value is ' ' or '\t' or '\n' or '\r' || cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || r.Value is 0x85;

    private static bool IsControl(UnicodeCategory cat) =>
        cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;

    private static bool IsPunctuation(Rune r, UnicodeCategory cat)
    {
        var cp = r.Value;
        if (cp is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126) return true;
        return cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    internal static bool IsCjk(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;

    private static string StripAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }
}

/// <summary>
/// XLM-RoBERTa tokenization: the SentencePiece Unigram model (Microsoft.ML.Tokenizers) with the fairseq id layout
/// Hugging Face uses — &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3, every other piece at its SentencePiece id + 1.
/// Verified token-for-token against the Hugging Face tokenizer on 2026-11-10.
/// </summary>
public sealed class XlmRobertaTokenizer : ITextTokenizer
{
    public const int Bos = 0, Pad = 1, Eos = 2, Unk = 3;
    private readonly SentencePieceTokenizer _sp;

    public XlmRobertaTokenizer(SentencePieceTokenizer sp) => _sp = sp;

    public static XlmRobertaTokenizer FromFile(string modelPath)
    {
        using var fs = File.OpenRead(modelPath);
        return new XlmRobertaTokenizer(SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false, addEndOfSentence: false));
    }

    public int PadId => Pad;

    public IReadOnlyList<int> Encode(string text, int maxTokens)
    {
        var raw = _sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var budget = Math.Max(1, maxTokens - 2);
        var ids = new List<int>(Math.Min(raw.Count, budget) + 2) { Bos };
        foreach (var id in raw)
        {
            if (ids.Count - 1 >= budget) break;
            ids.Add(id == _sp.UnknownId ? Unk : id + 1);
        }
        ids.Add(Eos);
        return ids;
    }
}
