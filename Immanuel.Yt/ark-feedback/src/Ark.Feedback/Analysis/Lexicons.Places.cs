namespace Ark.Feedback.Analysis;

/// <summary>Where a language is mostly spoken, where a country sits, and the names people use for places in comments.</summary>
public static partial class Lexicons
{
    public static readonly IReadOnlyDictionary<string, string> RegionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["north-america"] = "North America",
        ["latin-america"] = "Latin America & Caribbean",
        ["western-europe"] = "Western & Southern Europe",
        ["eastern-europe"] = "Central & Eastern Europe",
        ["mena"] = "Middle East & North Africa",
        ["africa"] = "Sub-Saharan Africa",
        ["south-asia"] = "South Asia",
        ["southeast-asia"] = "Southeast Asia",
        ["east-asia"] = "East Asia",
        ["central-asia"] = "Central Asia & Caucasus",
        ["oceania"] = "Oceania",
    };

    /// <summary>Language code → the region most of its speakers live in. English, Esperanto and Latin can't be placed.</summary>
    public static readonly IReadOnlyDictionary<string, string> LanguageRegion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Latin America (Spanish and Portuguese are counted here; Spain and Portugal show up in the country list when named)
        ["es"] = "latin-america", ["pt"] = "latin-america", ["ht"] = "latin-america", ["qu"] = "latin-america", ["gn"] = "latin-america", ["ay"] = "latin-america",
        // Western & Southern Europe
        ["fr"] = "western-europe", ["de"] = "western-europe", ["it"] = "western-europe", ["nl"] = "western-europe", ["sv"] = "western-europe", ["da"] = "western-europe",
        ["no"] = "western-europe", ["nb"] = "western-europe", ["nn"] = "western-europe", ["fi"] = "western-europe", ["is"] = "western-europe", ["ga"] = "western-europe",
        ["cy"] = "western-europe", ["gd"] = "western-europe", ["br"] = "western-europe", ["lb"] = "western-europe", ["als"] = "western-europe", ["bar"] = "western-europe",
        ["nds"] = "western-europe", ["pms"] = "western-europe", ["lmo"] = "western-europe", ["vec"] = "western-europe", ["scn"] = "western-europe", ["nap"] = "western-europe",
        ["eu"] = "western-europe", ["ca"] = "western-europe", ["gl"] = "western-europe", ["oc"] = "western-europe", ["fy"] = "western-europe", ["mt"] = "western-europe",
        ["fo"] = "western-europe", ["el"] = "western-europe", ["co"] = "western-europe", ["wa"] = "western-europe", ["li"] = "western-europe", ["rm"] = "western-europe",
        ["ast"] = "western-europe", ["an"] = "western-europe", ["sc"] = "western-europe", ["io"] = "western-europe", ["ia"] = "western-europe",
        // Central & Eastern Europe
        ["pl"] = "eastern-europe", ["cs"] = "eastern-europe", ["sk"] = "eastern-europe", ["hu"] = "eastern-europe", ["ro"] = "eastern-europe", ["bg"] = "eastern-europe",
        ["uk"] = "eastern-europe", ["be"] = "eastern-europe", ["ru"] = "eastern-europe", ["sr"] = "eastern-europe", ["hr"] = "eastern-europe", ["bs"] = "eastern-europe",
        ["sl"] = "eastern-europe", ["mk"] = "eastern-europe", ["sq"] = "eastern-europe", ["lt"] = "eastern-europe", ["lv"] = "eastern-europe", ["et"] = "eastern-europe",
        ["sh"] = "eastern-europe", ["hsb"] = "eastern-europe", ["dsb"] = "eastern-europe", ["rue"] = "eastern-europe", ["cv"] = "eastern-europe", ["tt"] = "eastern-europe",
        ["ba"] = "eastern-europe", ["myv"] = "eastern-europe", ["mhr"] = "eastern-europe", ["xmf"] = "eastern-europe", ["krc"] = "eastern-europe",
        // Middle East & North Africa
        ["ar"] = "mena", ["arz"] = "mena", ["fa"] = "mena", ["he"] = "mena", ["ku"] = "mena", ["ckb"] = "mena", ["azb"] = "mena", ["mzn"] = "mena", ["ps"] = "mena",
        ["tr"] = "mena", ["az"] = "mena", ["ug"] = "mena", ["gl"] = "western-europe", ["arc"] = "mena", ["yi"] = "mena", ["lrc"] = "mena", ["glk"] = "mena",
        // Sub-Saharan Africa
        ["sw"] = "africa", ["am"] = "africa", ["ha"] = "africa", ["yo"] = "africa", ["ig"] = "africa", ["zu"] = "africa", ["xh"] = "africa", ["so"] = "africa",
        ["rw"] = "africa", ["om"] = "africa", ["ti"] = "africa", ["sn"] = "africa", ["st"] = "africa", ["ln"] = "africa", ["lg"] = "africa", ["wo"] = "africa",
        ["ff"] = "africa", ["af"] = "africa", ["mg"] = "africa", ["ny"] = "africa", ["tn"] = "africa", ["ts"] = "africa", ["ve"] = "africa", ["ss"] = "africa",
        ["nr"] = "africa", ["kg"] = "africa", ["ee"] = "africa", ["tw"] = "africa", ["ak"] = "africa", ["rn"] = "africa", ["ki"] = "africa", ["lu"] = "africa",
        ["ny"] = "africa", ["sg"] = "africa", ["bm"] = "africa", ["kab"] = "mena",
        // South Asia
        ["hi"] = "south-asia", ["bn"] = "south-asia", ["ta"] = "south-asia", ["te"] = "south-asia", ["mr"] = "south-asia", ["gu"] = "south-asia", ["kn"] = "south-asia",
        ["ml"] = "south-asia", ["pa"] = "south-asia", ["ur"] = "south-asia", ["ne"] = "south-asia", ["si"] = "south-asia", ["or"] = "south-asia", ["as"] = "south-asia",
        ["sd"] = "south-asia", ["bh"] = "south-asia", ["mai"] = "south-asia", ["new"] = "south-asia", ["bpy"] = "south-asia", ["gom"] = "south-asia", ["sa"] = "south-asia",
        ["dv"] = "south-asia", ["pnb"] = "south-asia", ["dty"] = "south-asia", ["mni"] = "south-asia", ["ks"] = "south-asia", ["bo"] = "south-asia", ["dz"] = "south-asia",
        // Southeast Asia
        ["id"] = "southeast-asia", ["ms"] = "southeast-asia", ["vi"] = "southeast-asia", ["th"] = "southeast-asia", ["tl"] = "southeast-asia", ["ceb"] = "southeast-asia",
        ["war"] = "southeast-asia", ["jv"] = "southeast-asia", ["su"] = "southeast-asia", ["min"] = "southeast-asia", ["ilo"] = "southeast-asia", ["my"] = "southeast-asia",
        ["km"] = "southeast-asia", ["lo"] = "southeast-asia", ["bcl"] = "southeast-asia", ["pam"] = "southeast-asia", ["ace"] = "southeast-asia", ["bjn"] = "southeast-asia",
        ["map-bms"] = "southeast-asia", ["gor"] = "southeast-asia",
        // East Asia
        ["zh"] = "east-asia", ["ja"] = "east-asia", ["ko"] = "east-asia", ["yue"] = "east-asia", ["wuu"] = "east-asia", ["mn"] = "east-asia", ["hak"] = "east-asia", ["nan"] = "east-asia",
        // Central Asia & Caucasus
        ["kk"] = "central-asia", ["ky"] = "central-asia", ["uz"] = "central-asia", ["tk"] = "central-asia", ["tg"] = "central-asia", ["hy"] = "central-asia", ["ka"] = "central-asia",
        ["os"] = "central-asia", ["ce"] = "central-asia", ["av"] = "central-asia", ["lez"] = "central-asia", ["sah"] = "central-asia", ["kv"] = "central-asia", ["xal"] = "central-asia",
        // Oceania
        ["mi"] = "oceania", ["sm"] = "oceania", ["to"] = "oceania", ["fj"] = "oceania", ["haw"] = "oceania", ["ty"] = "oceania", ["tpi"] = "oceania",
    };

    private static Place P(string code, string name, string region, string aliases, string demonyms = "") =>
        new(code, name, region, aliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), demonyms.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Countries (and a few places that stand in for one) with the names and demonyms comments use. Demonyms that double as language names are weighed less.</summary>
    public static readonly IReadOnlyList<Place> Places =
    [
        // North America
        P("US", "United States", "north-america", "usa|u.s.a|u.s|united states|america|estados unidos|new york|nyc|los angeles|california|texas|florida|chicago|houston|miami|atlanta|seattle|boston|vegas|las vegas|washington dc|philadelphia|detroit|new jersey|michigan|georgia|arizona|minnesota|wisconsin|tennessee|kentucky|alabama|louisiana|oklahoma|oregon|utah|hawaii|alaska|estados unidos|états-unis|etats-unis|usa|अमेरिका|アメリカ|米国|미국|美国|美國|америка|сша|ابوالولايات|امريكا|أمريكا", "american|americans|americano|americana|américain|amerikaner|estadounidense"),
        P("CA", "Canada", "north-america", "canada|canadá|toronto|vancouver|montreal|montréal|ottawa|calgary|edmonton|quebec|québec|alberta|ontario|british columbia|winnipeg|कनाडा|カナダ|캐나다|加拿大|канада|كندا", "canadian|canadians|canadien|canadiense|kanadier"),
        P("MX", "Mexico", "latin-america", "mexico|méxico|mejico|cdmx|mexico city|ciudad de mexico|guadalajara|monterrey|puebla|tijuana|cancun|cancún|oaxaca|chiapas|jalisco|sinaloa|veracruz|yucatan|yucatán|मेक्सिको|メキシコ|멕시코|墨西哥|мексика|المكسيك", "mexican|mexicans|mexicano|mexicana|mexicanos|mexicanas|mexicain|mexikaner"),
        // Latin America & Caribbean
        P("BR", "Brazil", "latin-america", "brazil|brasil|brésil|brasilien|sao paulo|são paulo|rio de janeiro|brasilia|brasília|fortaleza|belo horizonte|curitiba|recife|porto alegre|manaus|bahia|minas gerais|ceará|paraná|pernambuco|nordeste|ब्राज़ील|ブラジル|브라질|巴西|бразилия|البرازيل", "brazilian|brazilians|brasileiro|brasileira|brasileiros|brasileiras|brésilien|brasilianer"),
        P("AR", "Argentina", "latin-america", "argentina|argentine|argentinien|buenos aires|cordoba|córdoba|rosario|mendoza|la plata|patagonia|アルゼンチン|아르헨티나|阿根廷|аргентина|الأرجنتين", "argentinian|argentine|argentino|argentina|argentinos|argentinas|argentinier"),
        P("CO", "Colombia", "latin-america", "colombia|colombie|kolumbien|bogota|bogotá|medellin|medellín|cali|barranquilla|cartagena|コロンビア|콜롬비아|哥伦比亚|колумбия|كولومبيا", "colombian|colombians|colombiano|colombiana|colombianos|colombianas"),
        P("CL", "Chile", "latin-america", "chile|chili|santiago de chile|valparaiso|valparaíso|チリ|칠레|智利|чили|تشيلي", "chilean|chileans|chileno|chilena|chilenos|chilenas"),
        P("PE", "Peru", "latin-america", "peru|perú|pérou|lima|cusco|cuzco|arequipa|machu picchu|ペルー|페루|秘鲁|перу|بيرو", "peruvian|peruvians|peruano|peruana|peruanos|peruanas"),
        P("VE", "Venezuela", "latin-america", "venezuela|caracas|maracaibo|valencia venezuela|ベネズエラ|베네수엘라|委内瑞拉|венесуэла|فنزويلا", "venezuelan|venezuelans|venezolano|venezolana|venezolanos|venezolanas"),
        P("EC", "Ecuador", "latin-america", "ecuador|équateur|quito|guayaquil|cuenca|エクアドル|에콰도르|厄瓜多尔|эквадор|الإكوادور", "ecuadorian|ecuatoriano|ecuatoriana|ecuatorianos"),
        P("BO", "Bolivia", "latin-america", "bolivia|bolivie|la paz|santa cruz de la sierra|cochabamba|ボリビア|볼리비아|玻利维亚|боливия", "bolivian|boliviano|boliviana|bolivianos"),
        P("PY", "Paraguay", "latin-america", "paraguay|asuncion|asunción|パラグアイ|파라과이|巴拉圭|парагвай", "paraguayan|paraguayo|paraguaya|paraguayos"),
        P("UY", "Uruguay", "latin-america", "uruguay|montevideo|ウルグアイ|우루과이|乌拉圭|уругвай", "uruguayan|uruguayo|uruguaya|uruguayos"),
        P("CU", "Cuba", "latin-america", "cuba|la habana|havana|キューバ|쿠바|古巴|куба|كوبا", "cuban|cubans|cubano|cubana|cubanos|cubanas"),
        P("DO", "Dominican Republic", "latin-america", "dominican republic|republica dominicana|república dominicana|santo domingo|rd|ドミニカ|도미니카|多米尼加|доминикана", "dominican|dominicans|dominicano|dominicana|dominicanos"),
        P("PR", "Puerto Rico", "latin-america", "puerto rico|san juan|pr|プエルトリコ|푸에르토리코|波多黎各|пуэрто-рико", "puerto rican|puerto ricans|boricua|boricuas|puertorriqueño|puertorriqueña"),
        P("GT", "Guatemala", "latin-america", "guatemala|グアテマラ|과테말라|危地马拉|гватемала", "guatemalan|guatemalteco|guatemalteca|chapin|chapín|chapines"),
        P("HN", "Honduras", "latin-america", "honduras|tegucigalpa|san pedro sula|ホンジュラス|온두라스|洪都拉斯|гондурас", "honduran|hondureño|hondureña|catracho|catracha|catrachos"),
        P("SV", "El Salvador", "latin-america", "el salvador|san salvador|エルサルバドル|엘살바도르|萨尔瓦多|сальвадор", "salvadoran|salvadoreño|salvadoreña|salvadoreños"),
        P("NI", "Nicaragua", "latin-america", "nicaragua|managua|ニカラグア|니카라과|尼加拉瓜|никарагуа", "nicaraguan|nicaragüense|nicaraguense|nica|nicas"),
        P("CR", "Costa Rica", "latin-america", "costa rica|san jose costa rica|コスタリカ|코스타리카|哥斯达黎加|коста-рика", "costa rican|costarricense|tico|tica|ticos"),
        P("PA", "Panama", "latin-america", "panama|panamá|パナマ|파나마|巴拿马|панама", "panamanian|panameño|panameña|panameños"),
        P("JM", "Jamaica", "latin-america", "jamaica|kingston jamaica|ジャマイカ|자메이카|牙买加|ямайка", "jamaican|jamaicans"),
        P("HT", "Haiti", "latin-america", "haiti|haïti|port-au-prince|ハイチ|아이티|海地|гаити", "haitian|haitians|haïtien|ayisyen"),
        P("TT", "Trinidad and Tobago", "latin-america", "trinidad|tobago|trinidad and tobago|port of spain", "trinidadian|trini|trinis"),
        // Western & Southern Europe
        P("GB", "United Kingdom", "western-europe", "uk|u.k|united kingdom|britain|great britain|england|scotland|wales|northern ireland|london|manchester|birmingham|liverpool|leeds|glasgow|edinburgh|cardiff|belfast|bristol|sheffield|newcastle|nottingham|reino unido|inglaterra|angleterre|royaume-uni|großbritannien|england|ब्रिटेन|इंग्लैंड|यूके|イギリス|英国|영국|英国|英國|британия|англия|لندن|بريطانيا|انجلترا", "british|brit|brits|english|scottish|welsh|irish|británico|britannique|inglés|inglesa|anglais|engländer|brite|briten"),
        P("IE", "Ireland", "western-europe", "ireland|éire|eire|irlanda|irlande|irland|dublin|cork|galway|limerick|アイルランド|아일랜드|爱尔兰|ирландия", "irish|irlandés|irlandais"),
        P("FR", "France", "western-europe", "france|francia|frankreich|paris|marseille|lyon|toulouse|nantes|strasbourg|bordeaux|lille|montpellier|フランス|프랑스|法国|法國|франция|فرنسا|फ्रांस", "french|francés|francesa|français|française|franzose|franzosen|francese"),
        P("DE", "Germany", "western-europe", "germany|deutschland|alemania|allemagne|alemanha|germania|berlin|munich|münchen|hamburg|frankfurt|cologne|köln|stuttgart|düsseldorf|dusseldorf|leipzig|dresden|bavaria|bayern|ドイツ|독일|德国|德國|германия|ألمانيا|जर्मनी", "german|germans|alemán|alemana|allemand|allemande|deutsche|deutscher|tedesco"),
        P("IT", "Italy", "western-europe", "italy|italia|italie|italien|rome|roma|milan|milano|naples|napoli|turin|torino|florence|firenze|venice|venezia|bologna|palermo|sicily|sicilia|sardinia|sardegna|イタリア|이탈리아|意大利|義大利|италия|إيطاليا|इटली", "italian|italians|italiano|italiana|italiani|italien|italienne|italiener"),
        P("ES", "Spain", "western-europe", "spain|españa|espana|espagne|spanien|espanha|spagna|madrid|barcelona|valencia|sevilla|seville|zaragoza|malaga|málaga|bilbao|andalucia|andalucía|andalusia|catalunya|cataluña|catalonia|canarias|canary islands|mallorca|galicia|スペイン|스페인|西班牙|испания|إسبانيا|स्पेन", "spaniard|spaniards|español|española|españoles|españolas|espagnol|espagnole|spanier"),
        P("PT", "Portugal", "western-europe", "portugal|lisbon|lisboa|porto|oporto|braga|coimbra|algarve|madeira|açores|azores|ポルトガル|포르투갈|葡萄牙|португалия|البرتغال", "portuguese|português|portuguesa|portugueses|portugais|portugiese|tuga|tugas"),
        P("NL", "Netherlands", "western-europe", "netherlands|the netherlands|holland|nederland|países bajos|paises bajos|pays-bas|niederlande|amsterdam|rotterdam|the hague|den haag|utrecht|eindhoven|オランダ|네덜란드|荷兰|нидерланды|голландия|هولندا", "dutch|nederlander|nederlanders|holandés|holandesa|néerlandais|niederländer"),
        P("BE", "Belgium", "western-europe", "belgium|belgique|belgië|belgie|bélgica|belgica|belgien|brussels|bruxelles|brussel|antwerp|antwerpen|anvers|ghent|gent|liège|liege|ベルギー|벨기에|比利时|бельгия", "belgian|belgians|belge|belg|belgen|belga"),
        P("CH", "Switzerland", "western-europe", "switzerland|schweiz|suisse|svizzera|suiza|suíça|zurich|zürich|geneva|genève|genf|basel|bern|lausanne|スイス|스위스|瑞士|швейцария|سويسرا", "swiss|schweizer|suisse|svizzero|suizo|suiza"),
        P("AT", "Austria", "western-europe", "austria|österreich|oesterreich|autriche|vienna|wien|graz|salzburg|innsbruck|linz|オーストリア|오스트리아|奥地利|австрия", "austrian|austrians|österreicher|autrichien|austriaco"),
        P("SE", "Sweden", "western-europe", "sweden|sverige|suecia|suède|schweden|stockholm|gothenburg|göteborg|malmö|malmo|uppsala|スウェーデン|스웨덴|瑞典|швеция", "swedish|swedes|svensk|svenskar|sueco|suédois|schwede"),
        P("NO", "Norway", "western-europe", "norway|norge|noreg|noruega|norvège|norwegen|oslo|bergen|trondheim|stavanger|ノルウェー|노르웨이|挪威|норвегия", "norwegian|norwegians|norsk|nordmann|noruego|norvégien|norweger"),
        P("DK", "Denmark", "western-europe", "denmark|danmark|dinamarca|danemark|dänemark|copenhagen|københavn|aarhus|odense|デンマーク|덴마크|丹麦|дания", "danish|danes|dansk|danskere|danés|danois|däne"),
        P("FI", "Finland", "western-europe", "finland|suomi|finlandia|finlande|finnland|helsinki|tampere|turku|oulu|フィンランド|핀란드|芬兰|финляндия", "finnish|finns|suomalainen|suomalaiset|finlandés|finlandais|finne"),
        P("IS", "Iceland", "western-europe", "iceland|ísland|island|islandia|islande|reykjavik|reykjavík|アイスランド|아이슬란드|冰岛|исландия", "icelandic|icelander|icelanders|íslendingur"),
        P("GR", "Greece", "western-europe", "greece|ελλάδα|ελλαδα|hellas|grecia|grèce|griechenland|athens|αθήνα|thessaloniki|θεσσαλονίκη|crete|κρήτη|santorini|mykonos|ギリシャ|그리스|希腊|греция|اليونان", "greek|greeks|έλληνας|ελληνας|έλληνες|griego|grec|grieche"),
        P("MT", "Malta", "western-europe", "malta|valletta", "maltese"),
        P("LU", "Luxembourg", "western-europe", "luxembourg|luxemburg|lëtzebuerg", "luxembourger|luxembourgish"),
        // Central & Eastern Europe
        P("RU", "Russia", "eastern-europe", "russia|россия|russie|rusia|russland|rússia|moscow|москва|saint petersburg|st petersburg|петербург|питер|спб|novosibirsk|новосибирск|yekaterinburg|екатеринбург|kazan|казань|sochi|сочи|siberia|сибирь|ロシア|러시아|俄罗斯|俄國|روسيا|रूस", "russian|russians|русский|русская|русские|россиянин|россияне|ruso|rusa|russe|russo"),
        P("UA", "Ukraine", "eastern-europe", "ukraine|україна|украина|ucrania|ukraina|kyiv|kiev|київ|киев|kharkiv|харків|харьков|odesa|odessa|одеса|одесса|lviv|львів|львов|dnipro|дніпро|ウクライナ|우크라이나|乌克兰|أوكرانيا|यूक्रेन", "ukrainian|ukrainians|українець|українка|українці|украинец|украинка|украинцы|ucraniano|ukrainien|ukrainer"),
        P("PL", "Poland", "eastern-europe", "poland|polska|polonia|pologne|polen|warsaw|warszawa|krakow|kraków|cracovia|gdansk|gdańsk|wroclaw|wrocław|poznan|poznań|lodz|łódź|ポーランド|폴란드|波兰|польша|بولندا|पोलैंड", "polish|polak|polka|polacy|polaco|polonais|polnisch"),
        P("CZ", "Czechia", "eastern-europe", "czechia|czech republic|česko|cesko|česká republika|chequia|tchéquie|tschechien|prague|praha|praga|brno|ostrava|チェコ|체코|捷克|чехия", "czech|czechs|čech|češi|checo|tchèque|tscheche"),
        P("SK", "Slovakia", "eastern-europe", "slovakia|slovensko|eslovaquia|slovaquie|slowakei|bratislava|košice|kosice|スロバキア|슬로바키아|斯洛伐克|словакия", "slovak|slovaks|slovák|slováci|eslovaco|slovaque"),
        P("HU", "Hungary", "eastern-europe", "hungary|magyarország|magyarorszag|hungría|hungria|hongrie|ungarn|budapest|debrecen|szeged|ハンガリー|헝가리|匈牙利|венгрия", "hungarian|hungarians|magyar|magyarok|húngaro|hongrois|ungar"),
        P("RO", "Romania", "eastern-europe", "romania|românia|rumania|rumanía|roumanie|rumänien|bucharest|bucurești|bucuresti|cluj|timișoara|timisoara|iași|iasi|transylvania|transilvania|ルーマニア|루마니아|罗马尼亚|румыния", "romanian|romanians|român|româncă|români|rumano|roumain|rumäne"),
        P("BG", "Bulgaria", "eastern-europe", "bulgaria|българия|bulgarie|bulgarien|sofia|софия|plovdiv|пловдив|varna|варна|ブルガリア|불가리아|保加利亚|болгария", "bulgarian|bulgarians|българин|българка|българи|búlgaro|bulgare|bulgare"),
        P("RS", "Serbia", "eastern-europe", "serbia|srbija|србија|serbie|serbien|belgrade|beograd|београд|novi sad|нови сад|niš|nis|セルビア|세르비아|塞尔维亚|сербия", "serbian|serbians|srbin|srpkinja|srbi|србин|срби|serbio|serbe"),
        P("HR", "Croatia", "eastern-europe", "croatia|hrvatska|croacia|croatie|kroatien|zagreb|split|rijeka|dubrovnik|dalmatia|dalmacija|クロアチア|크로아티아|克罗地亚|хорватия", "croatian|croatians|hrvat|hrvatica|hrvati|croata|croate|kroate"),
        P("BA", "Bosnia and Herzegovina", "eastern-europe", "bosnia|bosna|bosnia and herzegovina|bosna i hercegovina|sarajevo|mostar|banja luka|tuzla|ボスニア|보스니아|波斯尼亚|босния", "bosnian|bosnians|bosanac|bosanka|bosanci|bosnio|bosniaque"),
        P("SI", "Slovenia", "eastern-europe", "slovenia|slovenija|eslovenia|slovénie|slowenien|ljubljana|maribor|スロベニア|슬로베니아|斯洛文尼亚|словения", "slovenian|slovene|slovenec|slovenka|slovenci|esloveno|slovène"),
        P("MK", "North Macedonia", "eastern-europe", "macedonia|north macedonia|македонија|makedonija|skopje|скопје|マケドニア|마케도니아|马其顿|македония", "macedonian|macedonians|македонец|македонци"),
        P("AL", "Albania", "eastern-europe", "albania|shqipëri|shqiperi|shqipëria|albanie|albanien|tirana|tiranë|durrës|durres|アルバニア|알바니아|阿尔巴尼亚|албания", "albanian|albanians|shqiptar|shqiptare|shqiptarë|albanés|albanais|albaner"),
        P("XK", "Kosovo", "eastern-europe", "kosovo|kosova|kosovë|prishtina|pristina|prishtinë|косово", "kosovar|kosovars|kosovare"),
        P("ME", "Montenegro", "eastern-europe", "montenegro|crna gora|црна гора|podgorica|подгорица|черногория", "montenegrin|montenegrins|crnogorac|crnogorci"),
        P("LT", "Lithuania", "eastern-europe", "lithuania|lietuva|lituania|lituanie|litauen|vilnius|kaunas|klaipėda|klaipeda|リトアニア|리투아니아|立陶宛|литва", "lithuanian|lithuanians|lietuvis|lietuviai|lituano|lituanien|litauer"),
        P("LV", "Latvia", "eastern-europe", "latvia|latvija|letonia|lettonie|lettland|riga|rīga|daugavpils|ラトビア|라트비아|拉脱维亚|латвия", "latvian|latvians|latvietis|latvieši|letón|letton|lette"),
        P("EE", "Estonia", "eastern-europe", "estonia|eesti|estonie|estland|tallinn|tartu|エストニア|에스토니아|爱沙尼亚|эстония", "estonian|estonians|eestlane|eestlased|estonio|estonien"),
        P("BY", "Belarus", "eastern-europe", "belarus|беларусь|белоруссия|bielorrusia|biélorussie|weißrussland|minsk|мінск|минск|ベラルーシ|벨라루스|白俄罗斯", "belarusian|belarusians|беларус|белорус|белорусы|bielorruso|biélorusse"),
        P("MD", "Moldova", "eastern-europe", "moldova|moldavia|moldavie|moldawien|chișinău|chisinau|кишинёв|кишинев|молдова|молдавия", "moldovan|moldovans|moldovean|moldoveni|молдаванин|молдаване"),
        // Middle East & North Africa
        P("TR", "Türkiye", "mena", "turkey|türkiye|turkiye|turquía|turquia|turquie|türkei|turchia|istanbul|i̇stanbul|ankara|izmir|i̇zmir|antalya|bursa|adana|konya|gaziantep|trabzon|トルコ|터키|土耳其|турция|تركيا|तुर्की", "turkish|turk|turks|türk|türkler|turco|turca|turcos|turc|türke"),
        P("SA", "Saudi Arabia", "mena", "saudi|saudi arabia|ksa|السعودية|السعوديه|سعودية|riyadh|الرياض|jeddah|جدة|جده|mecca|makkah|مكة|مكه|medina|madinah|المدينة|dammam|الدمام|サウジアラビア|사우디|沙特|саудовская аравия|सऊदी", "saudi|saudis|سعودي|سعوديين|سعوديه"),
        P("AE", "United Arab Emirates", "mena", "uae|u.a.e|united arab emirates|emirates|الإمارات|الامارات|dubai|دبي|abu dhabi|أبوظبي|ابوظبي|sharjah|الشارقة|ajman|ドバイ|두바이|迪拜|阿联酋|оаэ|дубай|दुबई", "emirati|emiratis|إماراتي|اماراتي"),
        P("QA", "Qatar", "mena", "qatar|قطر|doha|الدوحة|الدوحه|カタール|카타르|卡塔尔|катар", "qatari|qataris|قطري"),
        P("KW", "Kuwait", "mena", "kuwait|الكويت|kuwait city|クウェート|쿠웨이트|科威特|кувейт", "kuwaiti|kuwaitis|كويتي"),
        P("BH", "Bahrain", "mena", "bahrain|البحرين|manama|المنامة|バーレーン|바레인|巴林|бахрейн", "bahraini|بحريني"),
        P("OM", "Oman", "mena", "oman|عمان|سلطنة عمان|muscat|مسقط|オマーン|오만|阿曼|оман", "omani|omanis|عماني"),
        P("YE", "Yemen", "mena", "yemen|اليمن|sanaa|sana'a|صنعاء|aden|عدن|イエメン|예멘|也门|йемен", "yemeni|yemenis|يمني"),
        P("IQ", "Iraq", "mena", "iraq|العراق|العراقي|baghdad|بغداد|basra|البصرة|mosul|الموصل|erbil|أربيل|اربيل|kurdistan|كردستان|イラク|이라크|伊拉克|ирак", "iraqi|iraqis|عراقي|عراقية|عراقيين"),
        P("SY", "Syria", "mena", "syria|سوريا|سورية|damascus|دمشق|aleppo|حلب|homs|حمص|idlib|إدلب|シリア|시리아|叙利亚|сирия", "syrian|syrians|سوري|سورية|سوريين"),
        P("LB", "Lebanon", "mena", "lebanon|لبنان|beirut|بيروت|tripoli lebanon|طرابلس|レバノン|레바논|黎巴嫩|ливан", "lebanese|لبناني|لبنانية|لبنانيين"),
        P("JO", "Jordan", "mena", "jordan|الأردن|الاردن|amman|عمّان|عمان الاردن|petra|البتراء|ヨルダン|요르단|约旦|иордания", "jordanian|jordanians|أردني|اردني|أردنية"),
        P("PS", "Palestine", "mena", "palestine|فلسطين|gaza|غزة|غزه|west bank|الضفة|ramallah|رام الله|jerusalem|القدس|hebron|الخليل|パレスチナ|팔레스타인|巴勒斯坦|палестина|फिलिस्तीन", "palestinian|palestinians|فلسطيني|فلسطينية|فلسطينيين"),
        P("IL", "Israel", "mena", "israel|ישראל|إسرائيل|اسرائيل|tel aviv|תל אביב|haifa|חיפה|jerusalem|ירושלים|イスラエル|이스라엘|以色列|израиль|इज़राइल|इजराइल", "israeli|israelis|ישראלי|ישראלים|إسرائيلي"),
        P("EG", "Egypt", "mena", "egypt|مصر|مصر|cairo|القاهرة|القاهره|alexandria|الإسكندرية|الاسكندرية|اسكندرية|giza|الجيزة|luxor|الأقصر|aswan|أسوان|エジプト|이집트|埃及|египет|मिस्र", "egyptian|egyptians|مصري|مصرية|مصريين|مصريه"),
        P("MA", "Morocco", "mena", "morocco|maroc|marruecos|المغرب|casablanca|الدار البيضاء|rabat|الرباط|marrakech|marrakesh|مراكش|fez|fès|فاس|tangier|tanger|طنجة|agadir|أكادير|モロッコ|모로코|摩洛哥|марокко", "moroccan|moroccans|marocain|marocaine|marroquí|مغربي|مغربية|مغاربة"),
        P("DZ", "Algeria", "mena", "algeria|algérie|algerie|argelia|الجزائر|algiers|alger|oran|وهران|constantine|قسنطينة|アルジェリア|알제리|阿尔及利亚|алжир", "algerian|algerians|algérien|algérienne|argelino|جزائري|جزائرية|جزائريين"),
        P("TN", "Tunisia", "mena", "tunisia|tunisie|túnez|tunez|تونس|tunis|sfax|صفاقس|sousse|سوسة|チュニジア|튀니지|突尼斯|тунис", "tunisian|tunisians|tunisien|tunisienne|tunecino|تونسي|تونسية"),
        P("LY", "Libya", "mena", "libya|libye|libia|ليبيا|tripoli|طرابلس|benghazi|بنغازي|リビア|리비아|利比亚|ливия", "libyan|libyans|ليبي|ليبية"),
        P("SD", "Sudan", "mena", "sudan|السودان|khartoum|الخرطوم|スーダン|수단|苏丹|судан", "sudanese|سوداني|سودانية"),
        P("IR", "Iran", "mena", "iran|ایران|إيران|ايران|tehran|تهران|طهران|isfahan|اصفهان|shiraz|شیراز|mashhad|مشهد|tabriz|تبریز|persia|イラン|이란|伊朗|иран|ईरान", "iranian|iranians|persian|persians|ایرانی|ایرانیان|إيراني|iraní"),
        P("AF", "Afghanistan", "mena", "afghanistan|افغانستان|kabul|کابل|كابل|herat|هرات|kandahar|قندهار|mazar|مزار|アフガニスタン|아프가니스탄|阿富汗|афганистан|अफगानिस्तान", "afghan|afghans|افغان|افغانی|afgano"),
        // Sub-Saharan Africa
        P("NG", "Nigeria", "africa", "nigeria|naija|9ja|lagos|abuja|kano|ibadan|port harcourt|benin city|enugu|kaduna|ナイジェリア|나이지리아|尼日利亚|нигерия|نيجيريا", "nigerian|nigerians"),
        P("GH", "Ghana", "africa", "ghana|accra|kumasi|takoradi|ガーナ|가나|加纳|гана|غانا", "ghanaian|ghanaians"),
        P("KE", "Kenya", "africa", "kenya|nairobi|mombasa|kisumu|nakuru|eldoret|ケニア|케냐|肯尼亚|кения|كينيا", "kenyan|kenyans|mkenya|wakenya"),
        P("ZA", "South Africa", "africa", "south africa|mzansi|suid-afrika|johannesburg|joburg|jozi|cape town|kaapstad|durban|pretoria|soweto|port elizabeth|gqeberha|bloemfontein|南アフリカ|남아공|南非|юар|جنوب أفريقيا|दक्षिण अफ्रीका", "south african|south africans|saffa|saffas"),
        P("ET", "Ethiopia", "africa", "ethiopia|ኢትዮጵያ|addis ababa|addis|አዲስ አበባ|エチオピア|에티오피아|埃塞俄比亚|эфиопия|إثيوبيا", "ethiopian|ethiopians|ኢትዮጵያዊ|habesha"),
        P("TZ", "Tanzania", "africa", "tanzania|dar es salaam|dodoma|arusha|zanzibar|mwanza|タンザニア|탄자니아|坦桑尼亚|танзания|تنزانيا", "tanzanian|tanzanians|mtanzania|watanzania"),
        P("UG", "Uganda", "africa", "uganda|kampala|entebbe|gulu|ウガンダ|우간다|乌干达|уганда|أوغندا", "ugandan|ugandans"),
        P("RW", "Rwanda", "africa", "rwanda|kigali|ルワンダ|르완다|卢旺达|руанда", "rwandan|rwandans|rwandese"),
        P("CM", "Cameroon", "africa", "cameroon|cameroun|camerún|yaoundé|yaounde|douala|カメルーン|카메룬|喀麦隆|камерун|الكاميرون", "cameroonian|cameroonians|camerounais|camerounaise"),
        P("CI", "Côte d'Ivoire", "africa", "ivory coast|côte d'ivoire|cote d'ivoire|cote divoire|abidjan|yamoussoukro|bouaké|コートジボワール|코트디부아르|科特迪瓦|кот-д'ивуар", "ivorian|ivorians|ivoirien|ivoirienne|ivoiriens"),
        P("SN", "Senegal", "africa", "senegal|sénégal|dakar|saint-louis senegal|touba|セネガル|세네갈|塞内加尔|сенегал|السنغال", "senegalese|sénégalais|sénégalaise"),
        P("ML", "Mali", "africa", "mali|bamako|timbuktu|tombouctou|マリ|말리|马里|мали", "malian|malians|malien|malienne"),
        P("CD", "DR Congo", "africa", "congo|drc|dr congo|rdc|kinshasa|lubumbashi|goma|bukavu|コンゴ|콩고|刚果|конго|الكونغو", "congolese|congolais|congolaise"),
        P("AO", "Angola", "africa", "angola|luanda|アンゴラ|앙골라|安哥拉|ангола", "angolan|angolans|angolano|angolana"),
        P("MZ", "Mozambique", "africa", "mozambique|moçambique|mocambique|maputo|beira|モザンビーク|모잠비크|莫桑比克|мозамбик", "mozambican|moçambicano|moçambicana"),
        P("ZW", "Zimbabwe", "africa", "zimbabwe|zim|harare|bulawayo|ジンバブエ|짐바브웨|津巴布韦|зимбабве", "zimbabwean|zimbabweans|zimbo|zimbos"),
        P("ZM", "Zambia", "africa", "zambia|lusaka|ndola|kitwe|ザンビア|잠비아|赞比亚|замбия", "zambian|zambians"),
        P("BW", "Botswana", "africa", "botswana|gaborone|ボツワナ|보츠와나|博茨瓦纳|ботсвана", "motswana|batswana"),
        P("NA", "Namibia", "africa", "namibia|windhoek|ナミビア|나미비아|纳米比亚|намибия", "namibian|namibians"),
        P("MW", "Malawi", "africa", "malawi|lilongwe|blantyre|マラウイ|말라위|马拉维|малави", "malawian|malawians"),
        P("SO", "Somalia", "africa", "somalia|soomaaliya|الصومال|mogadishu|muqdisho|مقديشو|hargeisa|somaliland|ソマリア|소말리아|索马里|сомали", "somali|somalis|soomaali|صومالي"),
        P("ER", "Eritrea", "africa", "eritrea|ኤርትራ|asmara|አስመራ|エリトリア|에리트레아|厄立特里亚|эритрея|إريتريا", "eritrean|eritreans|ኤርትራዊ"),
        P("MG", "Madagascar", "africa", "madagascar|madagasikara|antananarivo|tana|マダガスカル|마다가스카르|马达加斯加|мадагаскар", "malagasy|malgache"),
        P("GN", "Guinea", "africa", "guinea|guinée|guinee|conakry", "guinean|guinéen|guinéenne"),
        P("BF", "Burkina Faso", "africa", "burkina|burkina faso|ouagadougou|bobo-dioulasso", "burkinabe|burkinabé|burkinabè"),
        P("NE", "Niger", "africa", "niger|niamey|zinder", "nigerien|nigérien|nigérienne"),
        P("TD", "Chad", "africa", "chad|tchad|n'djamena|ndjamena|تشاد", "chadian|tchadien|tchadienne"),
        P("BJ", "Benin", "africa", "benin|bénin|cotonou|porto-novo", "beninese|béninois|béninoise"),
        P("TG", "Togo", "africa", "togo|lomé|lome", "togolese|togolais|togolaise"),
        P("SL", "Sierra Leone", "africa", "sierra leone|freetown|salone", "sierra leonean"),
        P("LR", "Liberia", "africa", "liberia|monrovia", "liberian|liberians"),
        P("GM", "Gambia", "africa", "gambia|the gambia|banjul|serekunda", "gambian|gambians"),
        P("SS", "South Sudan", "africa", "south sudan|juba", "south sudanese"),
        P("GA", "Gabon", "africa", "gabon|libreville", "gabonese|gabonais|gabonaise"),
        P("CG", "Republic of the Congo", "africa", "congo brazzaville|brazzaville|pointe-noire", "congolese"),
        P("MU", "Mauritius", "africa", "mauritius|maurice|île maurice|port louis", "mauritian|mauricien|mauricienne"),
        P("LS", "Lesotho", "africa", "lesotho|maseru", "mosotho|basotho"),
        P("SZ", "Eswatini", "africa", "eswatini|swaziland|mbabane", "swazi|emaswati"),
        P("BI", "Burundi", "africa", "burundi|bujumbura|gitega", "burundian|burundais"),
        P("DJ", "Djibouti", "africa", "djibouti|جيبوتي", "djiboutian|djiboutien"),
        P("MR", "Mauritania", "africa", "mauritania|mauritanie|موريتانيا|nouakchott|نواكشوط", "mauritanian|mauritanien|موريتاني"),
        P("CV", "Cape Verde", "africa", "cape verde|cabo verde|praia|mindelo", "cape verdean|cabo-verdiano|caboverdiano"),
        // South Asia
        P("IN", "India", "south-asia", "india|bharat|hindustan|भारत|इंडिया|हिंदुस्तान|ভারত|இந்தியா|భారతదేశం|ഇന്ത്യ|ભારત|ਭਾਰਤ|ଭାରତ|mumbai|bombay|मुंबई|delhi|new delhi|दिल्ली|bangalore|bengaluru|बैंगलोर|ಬೆಂಗಳೂರು|chennai|madras|चेन्नई|சென்னை|kolkata|calcutta|कोलकाता|কলকাতা|hyderabad|हैदराबाद|హైదరాబాద్|pune|पुणे|ahmedabad|अहमदाबाद|અમદાવાદ|jaipur|जयपुर|lucknow|लखनऊ|kanpur|nagpur|indore|bhopal|patna|पटना|chandigarh|kochi|cochin|kerala|केरल|കേരളം|tamil nadu|तमिलनाडु|தமிழ்நாடு|karnataka|कर्नाटक|maharashtra|महाराष्ट्र|gujarat|गुजरात|ગુજરાત|punjab|पंजाब|ਪੰਜਾਬ|rajasthan|राजस्थान|bihar|बिहार|uttar pradesh|उत्तर प्रदेश|bengal|west bengal|बंगाल|বাংলা|odisha|orissa|ओडिशा|assam|असम|অসম|telangana|तेलंगाना|andhra|आंध्र|goa|गोवा|kashmir|कश्मीर|himachal|उत्तराखंड|uttarakhand|jharkhand|chhattisgarh|haryana|हरियाणा|northeast india|インド|인도|印度|индия|الهند|هند", "indian|indians|desi|desis|भारतीय|हिंदुस्तानी|indiano|indio|india|indien|indier|indisch|tamilian|tamilians|malayali|malayalee|keralite|bengali|bengalis|punjabi|punjabis|gujarati|gujaratis|marathi|telugu|kannadiga|bihari|odia|assamese"),
        P("PK", "Pakistan", "south-asia", "pakistan|پاکستان|पाकिस्तान|karachi|کراچی|lahore|لاہور|islamabad|اسلام آباد|rawalpindi|راولپنڈی|peshawar|پشاور|quetta|کوئٹہ|multan|ملتان|faisalabad|فیصل آباد|sialkot|gujranwala|hyderabad sindh|sindh|سندھ|balochistan|بلوچستان|kpk|خیبر پختونخوا|gilgit|パキスタン|파키스탄|巴基斯坦|пакистан|باكستان", "pakistani|pakistanis|پاکستانی|पाकिस्तानी|paquistaní|pakistaner"),
        P("BD", "Bangladesh", "south-asia", "bangladesh|বাংলাদেশ|बांग्लादेश|dhaka|ঢাকা|chittagong|chattogram|চট্টগ্রাম|sylhet|সিলেট|khulna|খুলনা|rajshahi|রাজশাহী|バングラデシュ|방글라데시|孟加拉|бангладеш|بنغلاديش", "bangladeshi|bangladeshis|বাংলাদেশি|বাংলাদেশী|bangali"),
        P("LK", "Sri Lanka", "south-asia", "sri lanka|srilanka|ශ්‍රී ලංකා|இலங்கை|श्रीलंका|colombo|කොළඹ|கொழும்பு|මහනුවර|galle|jaffna|யாழ்ப்பாணம்|スリランカ|스리랑카|斯里兰卡|шри-ланка", "sri lankan|sri lankans|srilankan|lankan|lankans|ලාංකික|sinhalese"),
        P("NP", "Nepal", "south-asia", "nepal|नेपाल|kathmandu|काठमाडौं|काठमांडू|pokhara|पोखरा|ネパール|네팔|尼泊尔|непал", "nepali|nepalis|nepalese|नेपाली"),
        P("BT", "Bhutan", "south-asia", "bhutan|འབྲུག|भूटान|thimphu|ブータン|부탄|不丹|бутан", "bhutanese"),
        P("MV", "Maldives", "south-asia", "maldives|ދިވެހިރާއްޖެ|मालदीव|malé|male maldives|モルディブ|몰디브|马尔代夫|мальдивы", "maldivian|maldivians"),
        // Southeast Asia
        P("ID", "Indonesia", "southeast-asia", "indonesia|indo|nkri|jakarta|jkt|surabaya|bandung|medan|semarang|makassar|palembang|yogyakarta|jogja|yogya|bali|denpasar|lombok|sumatra|sumatera|jawa|kalimantan|borneo|sulawesi|papua|aceh|インドネシア|인도네시아|印尼|印度尼西亚|индонезия|إندونيسيا|इंडोनेशिया", "indonesian|indonesians|indonesio|orang indonesia|wni"),
        P("MY", "Malaysia", "southeast-asia", "malaysia|kuala lumpur|kl|penang|pulau pinang|johor|johor bahru|sarawak|melaka|malacca|ipoh|マレーシア|말레이시아|马来西亚|馬來西亞|малайзия|ماليزيا|मलेशिया", "malaysian|malaysians|orang malaysia"),
        P("SG", "Singapore", "southeast-asia", "singapore|singapura|சிங்கப்பூர்|新加坡|シンガポール|싱가포르|сингапур|سنغافورة|सिंगापुर", "singaporean|singaporeans"),
        P("PH", "Philippines", "southeast-asia", "philippines|pilipinas|filipinas|pinas|manila|maynila|quezon city|cebu|davao|makati|taguig|pasig|baguio|iloilo|bacolod|cagayan de oro|zamboanga|mindanao|visayas|luzon|batangas|cavite|bulacan|pampanga|フィリピン|필리핀|菲律宾|菲律賓|филиппины|الفلبين|फिलीपींस", "filipino|filipinos|filipina|filipinas|pinoy|pinoys|pinay|pinays|philippine|filipijns"),
        P("VN", "Vietnam", "southeast-asia", "vietnam|việt nam|viet nam|hanoi|hà nội|ha noi|ho chi minh|hồ chí minh|saigon|sài gòn|sai gon|da nang|đà nẵng|huế|hai phong|hải phòng|can tho|cần thơ|ベトナム|베트남|越南|вьетнам|فيتنام|वियतनाम", "vietnamese|người việt|vietnamita|vietnamien"),
        P("TH", "Thailand", "southeast-asia", "thailand|ประเทศไทย|เมืองไทย|ไทย|bangkok|กรุงเทพ|chiang mai|เชียงใหม่|phuket|ภูเก็ต|pattaya|พัทยา|タイ|태국|泰国|泰國|таиланд|تايلاند|थाईलैंड", "thai|thais|คนไทย|tailandés|thaïlandais"),
        P("MM", "Myanmar", "southeast-asia", "myanmar|burma|မြန်မာ|yangon|rangoon|ရန်ကုန်|mandalay|မန္တလေး|naypyidaw|ミャンマー|미얀마|缅甸|мьянма|ميانمار|म्यांमार", "burmese|myanmar people|မြန်မာလူမျိုး"),
        P("KH", "Cambodia", "southeast-asia", "cambodia|kampuchea|កម្ពុជា|phnom penh|ភ្នំពេញ|siem reap|សៀមរាប|カンボジア|캄보디아|柬埔寨|камбоджа|كمبوديا", "cambodian|cambodians|khmer|khmers|ខ្មែរ"),
        P("LA", "Laos", "southeast-asia", "laos|ລາວ|vientiane|ວຽງຈັນ|luang prabang|ラオス|라오스|老挝|лаос", "lao|laotian|laotians|ຄົນລາວ"),
        P("BN", "Brunei", "southeast-asia", "brunei|bandar seri begawan|ブルネイ|브루나이|文莱|бруней", "bruneian|bruneians"),
        P("TL", "Timor-Leste", "southeast-asia", "timor|timor-leste|east timor|dili", "timorese"),
        // East Asia
        P("CN", "China", "east-asia", "china|中国|中國|chine|cina|beijing|北京|shanghai|上海|guangzhou|广州|廣州|shenzhen|深圳|chengdu|成都|wuhan|武汉|hangzhou|杭州|nanjing|南京|chongqing|重庆|xi'an|xian|西安|tianjin|天津|suzhou|苏州|harbin|哈尔滨|guangdong|广东|sichuan|四川|中华|中華|中国人|大陆|大陸|中国大陆|中國大陸|중국|китай|الصين|चीन", "chinese|中国人|中國人|chino|china|chinois|chinese|chinesisch|华人|華人"),
        P("TW", "Taiwan", "east-asia", "taiwan|台湾|台灣|臺灣|taipei|台北|臺北|kaohsiung|高雄|taichung|台中|臺中|tainan|台南|臺南|formosa|台湾|대만|타이완|тайвань|تايوان|ताइवान", "taiwanese|台湾人|台灣人|臺灣人"),
        P("HK", "Hong Kong", "east-asia", "hong kong|hongkong|hk|香港|홍콩|гонконг|هونغ كونغ|हांगकांग", "hongkonger|hongkongers|hong konger|香港人|港人"),
        P("MO", "Macau", "east-asia", "macau|macao|澳门|澳門|마카오|макао", "macanese|澳门人|澳門人"),
        P("JP", "Japan", "east-asia", "japan|日本|japon|japón|giappone|tokyo|東京|osaka|大阪|kyoto|京都|nagoya|名古屋|yokohama|横浜|sapporo|札幌|fukuoka|福岡|神戸|hiroshima|広島|okinawa|沖縄|hokkaido|北海道|nihon|nippon|일본|япония|اليابان|जापान", "japanese|日本人|japonés|japonesa|japonais|giapponese|japaner|日本の|일본인"),
        P("KR", "South Korea", "east-asia", "korea|south korea|대한민국|한국|корея|seoul|서울|busan|부산|incheon|인천|daegu|대구|daejeon|대전|gwangju|광주|jeju|제주|韓国|韩国|corée|corea|coreia|südkorea|كوريا|कोरिया", "korean|koreans|한국인|한국 사람|coreano|coreana|coréen|coréenne|koreaner|韓国人|韩国人"),
        P("KP", "North Korea", "east-asia", "north korea|dprk|조선|북한|pyongyang|평양|北朝鮮|朝鲜|северная корея", "north korean|north koreans"),
        P("MN", "Mongolia", "east-asia", "mongolia|монгол|монгол улс|mongolie|mongolei|ulaanbaatar|улаанбаатар|モンゴル|몽골|蒙古|монголия|منغوليا|मंगोलिया", "mongolian|mongolians|монгол хүн|mongol"),
        // Central Asia & Caucasus
        P("KZ", "Kazakhstan", "central-asia", "kazakhstan|қазақстан|казахстан|almaty|алматы|astana|астана|nur-sultan|shymkent|шымкент|カザフスタン|카자흐스탄|哈萨克斯坦|كازاخستان", "kazakh|kazakhs|қазақ|казах|казахи|kazakhstani"),
        P("UZ", "Uzbekistan", "central-asia", "uzbekistan|o'zbekiston|ozbekiston|ўзбекистон|узбекистан|tashkent|toshkent|ташкент|samarkand|samarqand|самарканд|bukhara|buxoro|бухара|ウズベキスタン|우즈베키스탄|乌兹别克斯坦|أوزبكستان", "uzbek|uzbeks|o'zbek|ozbek|узбек|узбеки"),
        P("KG", "Kyrgyzstan", "central-asia", "kyrgyzstan|кыргызстан|киргизия|bishkek|бишкек|osh|ош|キルギス|키르기스스탄|吉尔吉斯斯坦", "kyrgyz|кыргыз|киргиз|киргизы"),
        P("TJ", "Tajikistan", "central-asia", "tajikistan|тоҷикистон|таджикистан|dushanbe|душанбе|タジキスタン|타지키스탄|塔吉克斯坦", "tajik|tajiks|тоҷик|таджик|таджики"),
        P("TM", "Turkmenistan", "central-asia", "turkmenistan|türkmenistan|туркменистан|ashgabat|ашхабад|トルクメニスタン|투르크메니스탄|土库曼斯坦", "turkmen|туркмен|туркмены"),
        P("AZ", "Azerbaijan", "central-asia", "azerbaijan|azərbaycan|azerbaycan|азербайджан|baku|bakı|баку|ganja azerbaijan|gəncə|アゼルバイジャン|아제르바이잔|阿塞拜疆|أذربيجان", "azerbaijani|azerbaijanis|azeri|azeris|azərbaycanlı|азербайджанец|азербайджанцы"),
        P("AM", "Armenia", "central-asia", "armenia|հայաստան|армения|yerevan|երևան|ереван|gyumri|գյումրի|アルメニア|아르메니아|亚美尼亚|أرمينيا", "armenian|armenians|հայ|հայեր|армянин|армяне"),
        P("GE", "Georgia", "central-asia", "georgia country|saqartvelo|საქართველო|грузия|tbilisi|თბილისი|тбилиси|batumi|ბათუმი|батуми|kutaisi|ქუთაისი|ジョージア|조지아|格鲁吉亚", "georgian|georgians|ქართველი|ქართველები|грузин|грузины"),
        // Oceania
        P("AU", "Australia", "oceania", "australia|aussie land|down under|straya|sydney|melbourne|brisbane|perth|adelaide|canberra|gold coast|hobart|queensland|qld|nsw|new south wales|victoria australia|tasmania|tassie|オーストラリア|호주|澳大利亚|澳洲|австралия|أستراليا|ऑस्ट्रेलिया", "australian|australians|aussie|aussies|australiano|australien|australier"),
        P("NZ", "New Zealand", "oceania", "new zealand|nz|aotearoa|auckland|wellington|christchurch|hamilton nz|dunedin|tauranga|queenstown|ニュージーランド|뉴질랜드|新西兰|紐西蘭|новая зеландия|نيوزيلندا|न्यूज़ीलैंड", "new zealander|new zealanders|kiwi|kiwis|māori|maori"),
        P("FJ", "Fiji", "oceania", "fiji|viti|suva|フィジー|피지|斐济|фиджи", "fijian|fijians"),
        P("PG", "Papua New Guinea", "oceania", "papua new guinea|png|port moresby|lae", "papua new guinean|papuan"),
        P("WS", "Samoa", "oceania", "samoa|apia", "samoan|samoans"),
        P("TO", "Tonga", "oceania", "tonga|nuku'alofa|nukualofa", "tongan|tongans"),
    ];

    /// <summary>Demonyms that are also language names or everyday words; they count for half and not when the comment talks about subtitles, lyrics or speaking.</summary>
    public static readonly HashSet<string> AmbiguousDemonyms = new(StringComparer.Ordinal)
    {
        "english", "spanish", "french", "german", "italian", "portuguese", "russian", "japanese", "korean", "chinese", "arabic", "turkish", "vietnamese", "thai", "polish", "greek", "swedish",
        "dutch", "danish", "finnish", "norwegian", "hungarian", "romanian", "czech", "bulgarian", "ukrainian", "indonesian", "malaysian", "persian", "hindi", "tamil", "bengali", "punjabi",
        "gujarati", "marathi", "telugu", "kannada", "malayalam", "urdu", "nepali", "sinhalese", "burmese", "khmer", "lao", "mongolian", "georgian", "armenian", "azerbaijani", "kazakh",
        "uzbek", "somali", "swahili", "amharic", "malagasy", "irish", "welsh", "scottish", "icelandic", "albanian", "serbian", "croatian", "bosnian", "slovenian", "slovak", "lithuanian",
        "latvian", "estonian", "belarusian", "macedonian", "maltese", "hebrew", "yiddish", "kurdish", "pashto", "tagalog", "filipino", "javanese", "sundanese", "malay", "lao", "tibetan",
        "español", "española", "inglés", "inglesa", "francés", "francesa", "alemán", "alemana", "italiano", "italiana", "português", "portuguesa", "ruso", "rusa", "japonés", "japonesa",
        "coreano", "coreana", "chino", "china", "árabe", "turco", "turca", "anglais", "français", "française", "allemand", "allemande", "italien", "italienne", "espagnol", "espagnole",
        "portugais", "russe", "japonais", "coréen", "chinois", "arabe", "turc", "englisch", "deutsch", "deutsche", "deutscher", "spanisch", "französisch", "italienisch", "portugiesisch",
        "russisch", "japanisch", "koreanisch", "chinesisch", "arabisch", "türkisch", "inglese", "spagnolo", "francese", "tedesco", "portoghese", "giapponese", "coreano", "cinese", "arabo",
        "america", "lima", "mendoza", "shiraz", "milan", "florence", "kiwi", "madras", "bahia", "cali", "roma", "jordan", "chad", "niger", "mali", "guinea", "togo", "congo", "georgia", "turkey", "chile",
        "pr", "rd", "kl", "png", "zim", "tana", "lao", "german", "italian", "chino", "ruso", "rusa", "turco", "turca", "russian", "русский", "русская", "русские",
    };

    /// <summary>Words that make an ambiguous demonym about the language, not the place.</summary>
    public static readonly Lexicon LanguageTalk = new("subtitles", "subtitle", "subs", "sub", "lyrics", "lyric", "translation", "translate", "translated", "speak", "speaks", "speaking", "spoke", "language", "languages", "version", "dub", "dubbed", "accent", "learning", "learn", "pronunciation", "grammar", "class", "lesson", "subtítulos", "subtitulos", "letra", "traducción", "traduccion", "traducir", "hablar", "habla", "hablo", "idioma", "versión", "doblaje", "acento", "legendas", "legenda", "letra", "tradução", "traduzir", "falar", "fala", "falo", "idioma", "língua", "versão", "dublado", "sotaque", "sous-titres", "paroles", "traduction", "traduire", "parler", "parle", "langue", "version", "doublage", "accent", "untertitel", "songtext", "übersetzung", "übersetzen", "sprechen", "spricht", "sprache", "version", "synchron", "akzent", "sottotitoli", "testo", "traduzione", "tradurre", "parlare", "parla", "lingua", "versione", "doppiaggio", "accento", "subtitle", "terjemahan", "bahasa", "bicara", "versi", "altyazı", "çeviri", "konuşmak", "dil", "versiyon", "субтитры", "перевод", "говорить", "язык", "версия", "озвучка", "акцент", "सबटाइटल", "अनुवाद", "बोलना", "भाषा", "字幕", "翻訳", "話す", "言語", "자막", "번역", "말하다", "언어", "字幕", "翻译", "说", "语言");

    private static readonly Dictionary<string, Place> ByCode = Places.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

    public static Place? PlaceByCode(string code) => ByCode.TryGetValue(code, out var p) ? p : null;

    /// <summary>"Fr" → the region of France; a code we don't list → null.</summary>
    public static string? RegionOfCountry(string code) => PlaceByCode(code)?.Region;
}
