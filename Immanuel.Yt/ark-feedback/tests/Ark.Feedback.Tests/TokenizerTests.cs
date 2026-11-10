using System.Text.Json;
using Ark.Feedback.Nlp;

namespace Ark.Feedback.Tests;

public class TokenizerTests
{
    private static readonly string[] Vocab = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "Hello", "world", "##s", "un", "##aff", "##able", "!", "日", "本", "été", "a"];

    private static WordPieceTokenizer Tok() => new(Vocab);

    [Fact]
    public void WordPieceSplitsOnWhitespaceAndPunctuationAndContinuations()
    {
        var ids = Tok().Encode("Hello worlds! unaffable", 32);
        Assert.Equal([2, 5, 6, 7, 11, 8, 9, 10, 3], ids);
    }

    [Fact]
    public void WordPieceEmitsUnkForUnknownWordsAndEmoji()
    {
        var ids = Tok().Encode("Hello nope 😍", 32);
        Assert.Equal([2, 5, 1, 1, 3], ids);
    }

    [Fact]
    public void WordPieceSplitsCjkCharactersAndKeepsAccentsWhenCased()
    {
        Assert.Equal([2, 12, 13, 3], Tok().Encode("日本", 32));
        Assert.Equal([2, 14, 3], Tok().Encode("été", 32));
        Assert.Equal([2, 1, 3], Tok().Encode("ete", 32)); // not in the vocab: no accent folding for a cased model
    }

    [Fact]
    public void WordPieceDropsControlCharactersAndTruncates()
    {
        Assert.Equal([2, 5, 6, 3], Tok().Encode("Hel\u0000lo\u200b wor\u0001ld", 32)); // NUL, zero-width space and controls are stripped, the words survive
        var ids = Tok().Encode("Hello Hello Hello Hello Hello", 5);
        Assert.Equal(5, ids.Count);
        Assert.Equal(2, ids[0]);
        Assert.Equal(3, ids[^1]);
    }

    [Fact]
    public void WordPieceVeryLongWordIsUnk()
    {
        var ids = Tok().Encode(new string('a', 120), 32);
        Assert.Equal([2, 1, 3], ids);
    }

    /// <summary>Token-for-token agreement with the Hugging Face tokenizers library (reference ids computed 2026-11-10) when the real files are present.</summary>
    [Theory]
    [InlineData("distil", "distilbert-multilingual-sentiment", "vocab.txt")]
    [InlineData("tox", "distilbert-multilingual-toxicity", "vocab.txt")]
    [InlineData("goemo", "mbert-multilingual-go-emotions", "vocab.txt")]
    public void WordPieceMatchesHuggingFaceReference(string name, string slug, string file)
    {
        var vocab = Files.ModelFile(slug, file);
        if (vocab is null) return; // model files not on this machine
        var tok = WordPieceTokenizer.FromFile(vocab);
        var (texts, ids) = Reference(name);
        for (var i = 0; i < texts.Length; i++)
            Assert.Equal(ids[i], tok.Encode(texts[i], 512));
    }

    [Theory]
    [InlineData("xlmt", "xlm-t-sentiment")]
    [InlineData("minilm", "paraphrase-multilingual-minilm")]
    public void XlmRobertaMatchesHuggingFaceReference(string name, string slug)
    {
        var spm = Files.ModelFile(slug, "sentencepiece.bpe.model");
        if (spm is null) return;
        var tok = XlmRobertaTokenizer.FromFile(spm);
        var (texts, ids) = Reference(name);
        for (var i = 0; i < texts.Length; i++)
            Assert.Equal(ids[i], tok.Encode(texts[i], 512));
    }

    private static (string[] Texts, int[][] Ids) Reference(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Files.TestData("ref_tokens.json")));
        var texts = doc.RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var ids = doc.RootElement.GetProperty("ids").GetProperty(name).EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray();
        return (texts, ids);
    }
}
