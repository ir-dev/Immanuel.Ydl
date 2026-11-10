using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ark.Feedback.Nlp;

/// <summary>Text clean-up, word splitting and the stop-word lists used for keywords and themes.</summary>
public static partial class TextTools
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRx();

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"(\p{L})\1{2,}")]
    private static partial Regex RepeatRx();

    [GeneratedRegex(@"[\p{L}\p{M}][\p{L}\p{M}\p{Nd}'’\-]*")]
    private static partial Regex WordRx();

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])@[\p{L}\p{Nd}_.\-]{2,}")]
    private static partial Regex MentionRx();

    [GeneratedRegex(@"\p{Nd}.*\p{Nd}")]
    private static partial Regex TwoDigitsRx();

    /// <summary>What the models see: whitespace collapsed, links replaced by a placeholder word, zero-width characters removed.</summary>
    public static string Clean(string text)
    {
        var t = text.Replace("​", "").Replace("‌", "").Replace("﻿", "");
        t = UrlRx().Replace(t, "http");
        t = SpacesRx().Replace(t, " ").Trim();
        return t;
    }

    /// <summary>A key under which two comments count as the same comment: lower-cased letters and digits only, elongations squashed ("sooo" → "so").</summary>
    public static string DedupKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.ToLowerInvariant().EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherSymbol or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                sb.Append(r.ToString());
        }
        return RepeatRx().Replace(sb.ToString(), "$1");
    }

    /// <summary>Words for keyword counting: lower-cased, stop words and 1-letter tokens dropped, CJK runs split into bigrams.</summary>
    public static IEnumerable<string> Words(string text, string lang)
    {
        var stop = Stopwords.For(lang);
        foreach (Match m in WordRx().Matches(text))
        {
            var w = m.Value.ToLowerInvariant().Trim('\'', '’', '-');
            if (w.Length == 0) continue;
            var first = w.EnumerateRunes().First().Value;
            if (WordPieceTokenizer.IsCjk(first) || LanguageDetector.ScriptOf(first) is "Jpan" or "Thai")
            {
                var runes = w.EnumerateRunes().ToArray();
                if (runes.Length == 1) { if (!stop.Contains(w)) yield return w; continue; }
                for (var i = 0; i + 1 < runes.Length; i++)
                {
                    var bg = runes[i].ToString() + runes[i + 1];
                    if (!stop.Contains(bg)) yield return bg;
                }
                continue;
            }
            if (w.Length < 2 || stop.Contains(w) || Stopwords.Common.Contains(w)) continue;
            if (w.All(char.IsDigit) || TwoDigitsRx().IsMatch(w)) continue; // numbers and handle-like tokens ("mirela1894")
            yield return w;
        }
    }

    /// <summary>Emoji and pictographs in a text (the ones that carry a mood), skin-tone and variation selectors stripped.</summary>
    public static IEnumerable<string> Emoji(string text)
    {
        foreach (var r in text.EnumerateRunes())
        {
            var cp = r.Value;
            if (cp is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x1F1E6 and <= 0x1F1FF or 0x2764 or 0x2B50 or 0x203C or 0x2049)
            {
                if (cp is >= 0x1F3FB and <= 0x1F3FF) continue; // skin tones
                yield return r.ToString();
            }
        }
    }

    public static bool IsMostlyEmojiOrSymbols(string text)
    {
        var letters = 0;
        var other = 0;
        foreach (var r in text.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber) letters++;
            else if (cat is UnicodeCategory.OtherSymbol) other++;
        }
        return letters == 0 && other > 0;
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}

/// <summary>Stop words for the languages YouTube comments most often come in, plus words every comment section shares.</summary>
public static class Stopwords
{
    public static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "video", "videos", "youtube", "channel", "subscribe", "subscribed", "like", "likes", "comment", "comments", "http", "https", "www", "com", "user",
        "lol", "lmao", "omg", "bro", "bruh", "guys", "guy", "watch", "watching", "watched", "just", "really", "one", "also", "get", "got", "much", "many", "still", "even",
        "ever", "every", "thing", "things", "something", "someone", "people", "way", "time", "yes", "yeah", "yep", "yup", "nope", "nah", "ok", "okay", "ive", "im", "dont", "cant", "didnt", "doesnt", "isnt", "wasnt",
        "fr", "tho", "ngl", "idk", "imo", "tbh", "rn", "smh", "wtf", "af", "pls", "plz", "thx", "ty", "xd", "haha", "hahaha", "jk", "btw", "ik", "ikr", "fyi", "cuz", "bc", "wow", "wait",
        "oh", "ah", "oof", "hi", "hello", "hey", "mee", "same", "edit", "reply", "replies", "says", "said", "say", "see", "know", "think", "mean", "actually", "literally", "gonna", "wanna", "kinda", "sorta",
        "here", "there", "now", "then", "today", "back", "come", "came", "going", "went", "go", "make", "made", "take", "took", "look", "looks", "looking", "thanks", "thank", "please",
    };

    private static readonly Dictionary<string, HashSet<string>> Lists = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = Set("a an the and or but if then so because as of at by for with about against between into through during before after above below to from up down in out on off over under again further once here there when where why how all any both each few more most other some such no nor not only own same than too very s t can will would should could may might must shall do does did doing have has had having be is are was were been being i me my myself we our ours ourselves you your yours yourself yourselves he him his himself she her hers herself it its itself they them their theirs themselves what which who whom this that these those am were i'm you're he's she's it's we're they're i've you've we've they've i'd you'd he'd she'd we'd they'd i'll you'll he'll she'll we'll they'll isn't aren't wasn't weren't hasn't haven't hadn't doesn't don't didn't won't wouldn't shan't shouldn't can't cannot couldn't mustn't let's that's who's what's here's there's when's where's why's how's u ur r ya gonna wanna gotna"),
        ["es"] = Set("de la que el en y a los del se las por un para con no una su al lo como más pero sus le ya o este sí porque esta entre cuando muy sin sobre también me hasta hay donde quien desde todo nos durante todos uno les ni contra otros ese eso ante ellos e esto mí antes algunos qué unos yo otro otras otra él tanto esa estos mucho quienes nada muchos cual poco ella estar estas algunas algo nosotros mi mis tú te ti tu tus ellas nosotras vosotros vosotras os mío mía míos mías tuyo tuya tuyos tuyas suyo suya suyos suyas nuestro nuestra nuestros nuestras vuestro vuestra vuestros vuestras esos esas estoy estás está estamos estáis están esté estés estemos estéis estén estaré estarás estará estaremos estaréis estarán es son soy eres somos sois fue fui era eran he has ha hemos habéis han hay"),
        ["pt"] = Set("de a o que e do da em um para é com não uma os no se na por mais as dos como mas foi ao ele das tem à seu sua ou ser quando muito há nos já está eu também só pelo pela até isso ela entre era depois sem mesmo aos ter seus quem nas me esse eles estão você tinha foram essa num nem suas meu às minha têm numa pelos elas havia seja qual será nós tenho lhe deles essas esses pelas este fosse dele tu te vocês vos lhes meus minhas teu tua teus tuas nosso nossa nossos nossas dela delas esta estes estas aquele aquela aqueles aquelas isto aquilo estou está estamos estão estive esteve estivemos estiveram tá né pra pro mto muita"),
        ["fr"] = Set("au aux avec ce ces dans de des du elle en et eux il je la le leur lui ma mais me même mes moi mon ne nos notre nous on ou par pas pour qu que qui sa se ses son sur ta te tes toi ton tu un une vos votre vous c d j l à m n s t y été étée étées étés étant suis es est sommes êtes sont serai seras sera serons serez seront serais serait serions seriez seraient étais était étions étiez étaient fus fut fûmes fûtes furent sois soit soyons soyez soient fusse fusses fût fussions fussiez fussent ayant eu eue eues eus ai as avons avez ont aurai auras aura aurons aurez auront aurais aurait aurions auriez auraient avais avait avions aviez avaient eut eûmes eûtes eurent aie aies ait ayons ayez aient eusse eusses eût eussions eussiez eussent cette très trop ça cela comme plus si bien tout tous"),
        ["de"] = Set("aber alle allem allen aller alles als also am an ander andere anderem anderen anderer anderes anderm andern anderr anders auch auf aus bei bin bis bist da damit dann der den des dem die das daß dass derselbe derselben denselben desselben demselben dieselbe dieselben dasselbe dazu dein deine deinem deinen deiner deines denn derer dessen dich dir du dies diese diesem diesen dieser dieses doch dort durch ein eine einem einen einer eines einig einige einigem einigen einiger einiges einmal er ihn ihm es etwas euer eure eurem euren eurer eures für gegen gewesen hab habe haben hat hatte hatten hier hin hinter ich mich mir ihr ihre ihrem ihren ihrer ihres euch im in indem ins ist jede jedem jeden jeder jedes jene jenem jenen jener jenes jetzt kann kein keine keinem keinen keiner keines können könnte machen man manche manchem manchen mancher manches mein meine meinem meinen meiner meines mit muss musste nach nicht nichts noch nun nur ob oder ohne sehr sein seine seinem seinen seiner seines selbst sich sie ihnen sind so solche solchem solchen solcher solches soll sollte sondern sonst über um und uns unsere unserem unseren unser unseres unter viel vom von vor während war waren warst was weg weil weiter welche welchem welchen welcher welches wenn werde werden wie wieder will wir wird wirst wo wollen wollte würde würden zu zum zur zwar zwischen"),
        ["it"] = Set("ad al allo ai agli all agl alla alle con col coi da dal dallo dai dagli dall dagl dalla dalle di del dello dei degli dell degl della delle in nel nello nei negli nell negl nella nelle su sul sullo sui sugli sull sugl sulla sulle per tra contro io tu lui lei noi voi loro mio mia miei mie tuo tua tuoi tue suo sua suoi sue nostro nostra nostri nostre vostro vostra vostri vostre mi ti ci vi lo la li le gli ne il un uno una ma ed se perché anche come dov dove che chi cui non più quale quanto quanti quanta quante quello quelli quella quelle questo questi questa queste si tutto tutti a c e i l o ho hai ha abbiamo avete hanno sono sei è siamo siete sia molto"),
        ["nl"] = Set("de en van ik te dat die in een hij het niet zijn is was op aan met als voor had er maar om hem dan zou of wat mijn men dit zo door over ze zich bij ook tot je mij uit der daar haar naar heb hoe heeft hebben deze u want nog zal me zij nu ge geen omdat iets worden toch al waren veel meer doen toen moet ben zonder kan hun dus alles onder ja eens hier wie werd altijd doch wordt wezen kunnen ons zelf tegen na reeds wil kon niets uw iemand geweest andere"),
        ["ru"] = Set("и в во не что он на я с со как а то все она так его но да ты к у же вы за бы по только ее мне было вот от меня еще нет о из ему теперь когда даже ну вдруг ли если уже или ни быть был него до вас нибудь опять уж вам ведь там потом себя ничего ей может они тут где есть надо ней для мы тебя их чем была сам чтоб без будто чего раз тоже себе под будет ж тогда кто этот того потому этого какой совсем ним здесь этом один почти мой тем чтобы нее сейчас были куда зачем всех никогда можно при наконец два об другой хоть после над больше тот через эти нас про всего них какая много разве три эту моя впрочем хорошо свою этой перед иногда лучше чуть том нельзя такой им более всегда конечно всю между это очень"),
        ["tr"] = Set("acaba ama aslında az bazı belki biri birkaç birşey biz bu çok çünkü da daha de defa diye eğer en gibi hem hep hepsi her hiç için ile ise kez ki kim mı mu mü nasıl ne neden nerde nerede nereye niçin niye o sanki şey siz şu tüm ve veya ya yani bir ben sen onlar bunu şunu bence çok kadar değil var yok mi"),
        ["id"] = Set("yang dan di ke dari ini itu dengan untuk pada adalah sebagai juga tidak akan ada atau oleh dalam bisa saya aku kamu dia mereka kita kami nya saja sudah telah lagi karena jika bagi lebih sangat seperti hanya apa siapa bagaimana kenapa dimana nih sih dong deh kok banget gak ga ya yg udah udh aja"),
        ["hi"] = Set("के का एक में की है यह और से हैं को पर इस होता कि जो कर मे गया करने किया लिये अपने ने बनी नहीं तो ही या एवं दिया हो इसका था द्वारा हुआ तक साथ करना वाले बाद लिए आप कुछ सकते किसी ये इसके सबसे इसमें थे दो होने वह वे करते बहुत कहा वर्ग कई करें होती अपनी उनके थी यदि हुई जा ना इसे कहते जब होते कोई हुए व न अभी जैसे सभी करता उनकी तरह उस आदि कुल एस रहा इसकी सकता रहे उनका इसी रखें अपना पे उसके भी मैं तुम हम तुम्हारा मेरा हमारा उसका उसकी है।"),
        ["ar"] = Set("في من على و إلى عن أن إن كان كانت هذا هذه ذلك تلك التي الذي ما لا لم لن ليس هو هي هم أنا أنت نحن كل بعض مع بين حتى أو ثم قد لقد إذا كما لأن عند منذ بل أي أيضا غير جدا هل يا اللي انا انت احنا هذي ايه ليش كيف وين فيه مش مو"),
        ["ja"] = Set("の に は を た が で て と し れ さ ある いる も する から な こと として い や れる など なっ ない この ため その あっ よう また もの という あり まで られ なる へ か だ これ によって により おり より による ず なり られる において ば なかっ なく しかし について せ だっ その後 できる それ う ので なお のみ でき き つ における および いう さらに でも ら たり その他 に関する たち ます ん なら に対して 特に せる 及び これら とき では にて ほか ながら うち そして とともに ただし かつて それぞれ または お ほど ものの に対する ほとんど と共に といった です とも ところ ここ です ます"),
        ["ko"] = Set("이 그 저 것 수 등 들 및 에서 의 를 을 은 는 가 에 와 과 도 로 으로 다 한 하 있 없 되 하다 있다 없다 되다 이다 아니다 그리고 그러나 하지만 그래서 또 또한 너무 정말 진짜 완전 그냥 근데 ㅋㅋ ㅋㅋㅋ ㅎㅎ"),
        ["vi"] = Set("và của có là được trong cho với không này một các những để đã như tôi bạn mình anh chị em họ nó thì mà ở từ về cũng rất lắm quá nhé ạ ơi nha đó đây ấy"),
        ["pl"] = Set("i w na z do się nie że jest to jak o a po co tak ale za od by był było była być są ma mam mamy ja ty on ona my wy oni ten ta te tym tego tej już tylko bardzo też czy no bo więc jeszcze może ich jego jej nas was mnie cię"),
    };

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);

    public static HashSet<string> For(string lang)
    {
        var code = lang.Length > 2 && lang[2] is '-' or '_' ? lang[..2] : lang;
        return Lists.TryGetValue(code, out var s) ? s : Lists.TryGetValue("en", out var en) && code == "und" ? en : Empty;
    }
}
