using System.Text;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 设备/镜头归一化：把厂商私有型号代码翻译成人能读的机身名
/// （<c>SONY</c> + <c>ILCE-7M4</c> → <c>Sony A7 IV</c>），未知型号退化为
/// "规范厂商名 + 清洗后的型号"。纯静态、无状态、可并发调用。
/// </summary>
public static class DeviceNormalizer
{
    private static readonly Dictionary<string, string> ModelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------------- Sony ----------------
        ["ILCE7M4"] = "Sony A7 IV", ["ILCE7M3"] = "Sony A7 III", ["ILCE7M2"] = "Sony A7 II",
        ["ILCE7"] = "Sony A7", ["ILCE7RM5"] = "Sony A7R V", ["ILCE7RM4"] = "Sony A7R IV",
        ["ILCE7RM3"] = "Sony A7R III", ["ILCE7RM2"] = "Sony A7R II", ["ILCE7R"] = "Sony A7R",
        ["ILCE7SM3"] = "Sony A7S III", ["ILCE7SM2"] = "Sony A7S II", ["ILCE7S"] = "Sony A7S",
        ["ILCE7C"] = "Sony A7C", ["ILCE7CL"] = "Sony A7C", ["ILCE7CM2"] = "Sony A7C II", ["ILCE7CR"] = "Sony A7CR",
        ["ILCE9M3"] = "Sony A9 III", ["ILCE9M2"] = "Sony A9 II", ["ILCE9"] = "Sony A9",
        ["ILCE1"] = "Sony A1", ["ILCE1M2"] = "Sony A1 II",
        ["ILCE6700"] = "Sony A6700", ["ILCE6600"] = "Sony A6600", ["ILCE6500"] = "Sony A6500",
        ["ILCE6400"] = "Sony A6400", ["ILCE6300"] = "Sony A6300", ["ILCE6100"] = "Sony A6100",
        ["ILCE6000"] = "Sony A6000", ["ILCE5100"] = "Sony A5100", ["ILCE5000"] = "Sony A5000",
        ["ILCE3000"] = "Sony A3000",
        ["NEX7"] = "Sony NEX-7", ["NEX6"] = "Sony NEX-6", ["NEX5N"] = "Sony NEX-5N",
        ["NEX5R"] = "Sony NEX-5R", ["NEX5T"] = "Sony NEX-5T", ["NEX3N"] = "Sony NEX-3N",
        ["SLTA99M2"] = "Sony A99 II", ["SLTA99"] = "Sony A99", ["SLTA77M2"] = "Sony A77 II",
        ["SLTA77"] = "Sony A77", ["SLTA58"] = "Sony A58", ["SLTA68"] = "Sony A68",
        ["DSCRX100M7"] = "Sony RX100 VII", ["DSCRX100M6"] = "Sony RX100 VI", ["DSCRX100M5"] = "Sony RX100 V",
        ["DSCRX100M5A"] = "Sony RX100 V", ["DSCRX100M4"] = "Sony RX100 IV", ["DSCRX100M3"] = "Sony RX100 III",
        ["DSCRX100M2"] = "Sony RX100 II", ["DSCRX100"] = "Sony RX100",
        ["DSCRX10M4"] = "Sony RX10 IV", ["DSCRX10M3"] = "Sony RX10 III", ["DSCRX10M2"] = "Sony RX10 II",
        ["DSCRX10"] = "Sony RX10",
        ["DSCRX1RM2"] = "Sony RX1R II", ["DSCRX1R"] = "Sony RX1R", ["DSCRX1"] = "Sony RX1",
        ["DSCWX500"] = "Sony WX500", ["DSCHX99"] = "Sony HX99", ["DSCHX90"] = "Sony HX90",
        ["ZV1"] = "Sony ZV-1", ["ZV1M2"] = "Sony ZV-1 II", ["ZVE10"] = "Sony ZV-E10", ["ZVE1"] = "Sony ZV-E1",
        ["FX3"] = "Sony FX3", ["FX30"] = "Sony FX30", ["FX6"] = "Sony FX6", ["FX9"] = "Sony FX9",
        ["A7M4"] = "Sony A7 IV", ["A7M3"] = "Sony A7 III", ["A7M2"] = "Sony A7 II", ["A7M5"] = "Sony A7 V",
        ["A7RM5"] = "Sony A7R V", ["A7RM4"] = "Sony A7R IV", ["A7RM3"] = "Sony A7R III",
        ["A7SM3"] = "Sony A7S III", ["A9M3"] = "Sony A9 III", ["A6700"] = "Sony A6700", ["A6600"] = "Sony A6600",

        // ---------------- Canon ----------------
        ["EOSR5"] = "Canon EOS R5", ["EOSR5C"] = "Canon EOS R5 C", ["EOSR6"] = "Canon EOS R6",
        ["EOSR6M2"] = "Canon EOS R6 Mark II", ["EOSR6MARKII"] = "Canon EOS R6 Mark II",
        ["EOSR"] = "Canon EOS R", ["EOSRP"] = "Canon EOS RP", ["EOSR3"] = "Canon EOS R3",
        ["EOSR7"] = "Canon EOS R7", ["EOSR8"] = "Canon EOS R8", ["EOSR10"] = "Canon EOS R10",
        ["EOSR50"] = "Canon EOS R50", ["EOSR100"] = "Canon EOS R100", ["EOSRS"] = "Canon EOS R5",
        ["EOS5DM4"] = "Canon EOS 5D Mark IV", ["EOS5DM3"] = "Canon EOS 5D Mark III",
        ["EOS5DM2"] = "Canon EOS 5D Mark II", ["EOS5D"] = "Canon EOS 5D",
        ["EOS5DSR"] = "Canon EOS 5DS R", ["EOS5DS"] = "Canon EOS 5DS",
        ["EOS6DM2"] = "Canon EOS 6D Mark II", ["EOS6D"] = "Canon EOS 6D",
        ["EOS7DM2"] = "Canon EOS 7D Mark II", ["EOS7D"] = "Canon EOS 7D",
        ["EOS90D"] = "Canon EOS 90D", ["EOS80D"] = "Canon EOS 80D", ["EOS70D"] = "Canon EOS 70D",
        ["EOS60D"] = "Canon EOS 60D", ["EOS77D"] = "Canon EOS 77D", ["EOS800D"] = "Canon EOS 800D",
        ["EOS760D"] = "Canon EOS 760D", ["EOS750D"] = "Canon EOS 750D", ["EOS700D"] = "Canon EOS 700D",
        ["EOS650D"] = "Canon EOS 650D", ["EOS600D"] = "Canon EOS 600D", ["EOS550D"] = "Canon EOS 550D",
        ["EOS500D"] = "Canon EOS 500D", ["EOS450D"] = "Canon EOS 450D", ["EOS400D"] = "Canon EOS 400D",
        ["EOS350D"] = "Canon EOS 350D", ["EOS300D"] = "Canon EOS 300D", ["EOS1000D"] = "Canon EOS 1000D",
        ["EOS200D"] = "Canon EOS 200D", ["EOS250D"] = "Canon EOS 250D",
        ["EOS1DXM3"] = "Canon EOS-1D X Mark III", ["EOS1DXM2"] = "Canon EOS-1D X Mark II",
        ["EOS1DX"] = "Canon EOS-1D X", ["EOS1DM4"] = "Canon EOS-1D Mark IV",
        ["EOSM50M2"] = "Canon EOS M50 Mark II", ["EOSM50"] = "Canon EOS M50",
        ["EOSM6M2"] = "Canon EOS M6 Mark II", ["EOSM6"] = "Canon EOS M6", ["EOSM5"] = "Canon EOS M5",
        ["EOSM200"] = "Canon EOS M200", ["EOSM100"] = "Canon EOS M100", ["EOSRA"] = "Canon EOS Ra",
        ["POWERSHOTG7X"] = "Canon PowerShot G7 X", ["POWERSHOTG7XM2"] = "Canon PowerShot G7 X Mark II",
        ["POWERSHOTG7XM3"] = "Canon PowerShot G7 X Mark III", ["POWERSHOTG5X"] = "Canon PowerShot G5 X",
        ["POWERSHOTSX70HS"] = "Canon PowerShot SX70 HS",

        // ---------------- Nikon ----------------
        ["Z9"] = "Nikon Z9", ["Z8"] = "Nikon Z 8", ["Z7"] = "Nikon Z7", ["Z7II"] = "Nikon Z7 II",
        ["Z72"] = "Nikon Z7 II", ["Z6"] = "Nikon Z6", ["Z6II"] = "Nikon Z6 II", ["Z62"] = "Nikon Z6 II",
        ["Z63"] = "Nikon Z6 III", ["Z5"] = "Nikon Z5", ["Z50"] = "Nikon Z50", ["Z50II"] = "Nikon Z50 II",
        ["Z502"] = "Nikon Z50 II", ["Z30"] = "Nikon Z30", ["ZFC"] = "Nikon Z fc",
        ["ZF"] = "Nikon Zf", ["Z6III"] = "Nikon Z6 III",
        ["D5"] = "Nikon D5", ["D6"] = "Nikon D6", ["D850"] = "Nikon D850", ["D810"] = "Nikon D810",
        ["D800"] = "Nikon D800", ["D800E"] = "Nikon D800E", ["D780"] = "Nikon D780", ["D750"] = "Nikon D750",
        ["D610"] = "Nikon D610", ["D600"] = "Nikon D600", ["D500"] = "Nikon D500", ["D7500"] = "Nikon D7500",
        ["D7200"] = "Nikon D7200", ["D7100"] = "Nikon D7100", ["D7000"] = "Nikon D7000",
        ["D5600"] = "Nikon D5600", ["D5500"] = "Nikon D5500", ["D5300"] = "Nikon D5300",
        ["D5200"] = "Nikon D5200", ["D5100"] = "Nikon D5100", ["D3500"] = "Nikon D3500",
        ["D3400"] = "Nikon D3400", ["D3300"] = "Nikon D3300", ["D3200"] = "Nikon D3200",
        ["D3100"] = "Nikon D3100", ["D90"] = "Nikon D90", ["D300S"] = "Nikon D300S",
        ["COOLPIXP1000"] = "Nikon Coolpix P1000", ["COOLPIXP950"] = "Nikon Coolpix P950",
        ["NIKON1J5"] = "Nikon 1 J5", ["NIKON1V3"] = "Nikon 1 V3",

        // ---------------- Fujifilm ----------------
        ["XT5"] = "Fujifilm X-T5", ["XT4"] = "Fujifilm X-T4", ["XT3"] = "Fujifilm X-T3",
        ["XT2"] = "Fujifilm X-T2", ["XT1"] = "Fujifilm X-T1", ["XT50"] = "Fujifilm X-T50",
        ["XT30"] = "Fujifilm X-T30", ["XT30II"] = "Fujifilm X-T30 II", ["XT20"] = "Fujifilm X-T20",
        ["XT10"] = "Fujifilm X-T10", ["XT200"] = "Fujifilm X-T200", ["XT100"] = "Fujifilm X-T100",
        ["XS20"] = "Fujifilm X-S20", ["XS10"] = "Fujifilm X-S10",
        ["XH2S"] = "Fujifilm X-H2S", ["XH2"] = "Fujifilm X-H2", ["XH1"] = "Fujifilm X-H1",
        ["XPRO3"] = "Fujifilm X-Pro3", ["XPRO2"] = "Fujifilm X-Pro2", ["XPRO1"] = "Fujifilm X-Pro1",
        ["XE4"] = "Fujifilm X-E4", ["XE3"] = "Fujifilm X-E3", ["XE2"] = "Fujifilm X-E2",
        ["X100VI"] = "Fujifilm X100VI", ["X100V"] = "Fujifilm X100V", ["X100F"] = "Fujifilm X100F",
        ["X100T"] = "Fujifilm X100T", ["X100S"] = "Fujifilm X100S", ["X100"] = "Fujifilm X100",
        ["GFX100II"] = "Fujifilm GFX 100 II", ["GFX100S"] = "Fujifilm GFX 100S", ["GFX100"] = "Fujifilm GFX 100",
        ["GFX50SII"] = "Fujifilm GFX 50S II", ["GFX50S"] = "Fujifilm GFX 50S", ["GFX50R"] = "Fujifilm GFX 50R",
        ["XA7"] = "Fujifilm X-A7", ["XA5"] = "Fujifilm X-A5",

        // ---------------- Panasonic ----------------
        ["DCS5M2"] = "Panasonic Lumix S5 II", ["DCS5M2X"] = "Panasonic Lumix S5 II X",
        ["DCS5"] = "Panasonic Lumix S5", ["DCS1R"] = "Panasonic Lumix S1R", ["DCS1"] = "Panasonic Lumix S1",
        ["DCS1H"] = "Panasonic Lumix S1H", ["DCS9"] = "Panasonic Lumix S9",
        ["DCGH6"] = "Panasonic Lumix GH6", ["DCGH5M2"] = "Panasonic Lumix GH5 II",
        ["DCGH5S"] = "Panasonic Lumix GH5S", ["DCGH5"] = "Panasonic Lumix GH5",
        ["DCGH4"] = "Panasonic Lumix GH4", ["DCGH3"] = "Panasonic Lumix GH3",
        ["DCG9M2"] = "Panasonic Lumix G9 II", ["DCG9"] = "Panasonic Lumix G9",
        ["DCG95"] = "Panasonic Lumix G95", ["DCG85"] = "Panasonic Lumix G85",
        ["DCGX85"] = "Panasonic Lumix GX85", ["DCGX9"] = "Panasonic Lumix GX9",
        ["DCLX100M2"] = "Panasonic Lumix LX100 II", ["DCLX100"] = "Panasonic Lumix LX100",
        ["DCTZ200"] = "Panasonic Lumix TZ200", ["DCTZ100"] = "Panasonic Lumix TZ100",
        ["DCFZ1000M2"] = "Panasonic Lumix FZ1000 II", ["DCFZ1000"] = "Panasonic Lumix FZ1000",
        ["DCFZ300"] = "Panasonic Lumix FZ300", ["DCZS80"] = "Panasonic Lumix ZS80",
        ["DCG100"] = "Panasonic Lumix G100",

        // ---------------- Leica ----------------
        ["M11"] = "Leica M11", ["M11M"] = "Leica M11 Monochrom", ["M10"] = "Leica M10",
        ["M10P"] = "Leica M10-P", ["M10R"] = "Leica M10-R", ["M10M"] = "Leica M10 Monochrom",
        ["Q3"] = "Leica Q3", ["Q2"] = "Leica Q2", ["Q"] = "Leica Q",
        ["SL3"] = "Leica SL3", ["SL2"] = "Leica SL2", ["SL2S"] = "Leica SL2-S", ["SL"] = "Leica SL",
        ["CL"] = "Leica CL", ["TL2"] = "Leica TL2", ["S3"] = "Leica S3", ["MP"] = "Leica MP",
        ["MA"] = "Leica M-A", ["DLUX8"] = "Leica D-Lux 8", ["DLUX7"] = "Leica D-Lux 7",
        ["VLUX5"] = "Leica V-Lux 5",

        // ---------------- DJI ----------------
        // DJI 的 EXIF Model 是 FC3582 这类内部型号代码，没有可靠的商用名映射表；
        // 强制翻译反而会让"同一台飞机"在不同固件下名字漂移，因此统一保留 "DJI + 型号代码"。

        // ---------------- GoPro ----------------
        ["HERO13BLACK"] = "GoPro HERO13 Black", ["HERO12BLACK"] = "GoPro HERO12 Black",
        ["HERO11BLACK"] = "GoPro HERO11 Black", ["HERO10BLACK"] = "GoPro HERO10 Black",
        ["HERO9BLACK"] = "GoPro HERO9 Black", ["HERO8BLACK"] = "GoPro HERO8 Black",
        ["HERO7BLACK"] = "GoPro HERO7 Black", ["HERO6BLACK"] = "GoPro HERO6 Black",
        ["HERO5BLACK"] = "GoPro HERO5 Black", ["HERO4BLACK"] = "GoPro HERO4 Black",
        ["MAX"] = "GoPro MAX", ["FUSION"] = "GoPro Fusion",

        // ---------------- Insta360 ----------------
        ["ONEX4"] = "Insta360 X4", ["ONEX3"] = "Insta360 X3", ["ONEX2"] = "Insta360 ONE X2",
        ["ONEX"] = "Insta360 ONE X", ["ONERS"] = "Insta360 ONE RS",
        ["ONERS1INCH360"] = "Insta360 ONE RS 1-Inch 360", ["GO3"] = "Insta360 GO 3",
        ["GO3S"] = "Insta360 GO 3S", ["GO2"] = "Insta360 GO 2", ["GO"] = "Insta360 GO",
        ["ACEPRO"] = "Insta360 Ace Pro", ["ACE"] = "Insta360 Ace", ["X3"] = "Insta360 X3",
        ["X4"] = "Insta360 X4",

        // ---------------- Apple ----------------
        ["IPHONE16PROMAX"] = "Apple iPhone 16 Pro Max", ["IPHONE16PRO"] = "Apple iPhone 16 Pro",
        ["IPHONE16PLUS"] = "Apple iPhone 16 Plus", ["IPHONE16"] = "Apple iPhone 16",
        ["IPHONE16E"] = "Apple iPhone 16e",
        ["IPHONE15PROMAX"] = "Apple iPhone 15 Pro Max", ["IPHONE15PRO"] = "Apple iPhone 15 Pro",
        ["IPHONE15PLUS"] = "Apple iPhone 15 Plus", ["IPHONE15"] = "Apple iPhone 15",
        ["IPHONE14PROMAX"] = "Apple iPhone 14 Pro Max", ["IPHONE14PRO"] = "Apple iPhone 14 Pro",
        ["IPHONE14PLUS"] = "Apple iPhone 14 Plus", ["IPHONE14"] = "Apple iPhone 14",
        ["IPHONE13PROMAX"] = "Apple iPhone 13 Pro Max", ["IPHONE13PRO"] = "Apple iPhone 13 Pro",
        ["IPHONE13"] = "Apple iPhone 13", ["IPHONE13MINI"] = "Apple iPhone 13 mini",
        ["IPHONE12PROMAX"] = "Apple iPhone 12 Pro Max", ["IPHONE12PRO"] = "Apple iPhone 12 Pro",
        ["IPHONE12"] = "Apple iPhone 12", ["IPHONE12MINI"] = "Apple iPhone 12 mini",
        ["IPHONE11PROMAX"] = "Apple iPhone 11 Pro Max", ["IPHONE11PRO"] = "Apple iPhone 11 Pro",
        ["IPHONE11"] = "Apple iPhone 11", ["IPHONEXSMAX"] = "Apple iPhone XS Max",
        ["IPHONEXS"] = "Apple iPhone XS", ["IPHONEXR"] = "Apple iPhone XR", ["IPHONEX"] = "Apple iPhone X",
        ["IPHONESE3"] = "Apple iPhone SE (3rd generation)", ["IPHONESE2"] = "Apple iPhone SE (2nd generation)",
        ["IPADPRO"] = "Apple iPad Pro", ["IPADPRO11"] = "Apple iPad Pro 11", ["IPADPRO129"] = "Apple iPad Pro 12.9",

        // ---------------- OM System / Olympus ----------------
        ["OM1"] = "OM System OM-1", ["OM1MARKII"] = "OM System OM-1 Mark II", ["OM1M2"] = "OM System OM-1 Mark II",
        ["OM5"] = "OM System OM-5", ["OM3"] = "OM System OM-3",
        ["EM1MARKIII"] = "Olympus OM-D E-M1 Mark III", ["EM1M3"] = "Olympus OM-D E-M1 Mark III",
        ["EM1MARKII"] = "Olympus OM-D E-M1 Mark II", ["EM1M2"] = "Olympus OM-D E-M1 Mark II",
        ["EM1"] = "Olympus OM-D E-M1", ["EM5MARKIII"] = "Olympus OM-D E-M5 Mark III",
        ["EM5M3"] = "Olympus OM-D E-M5 Mark III", ["EM5MARKII"] = "Olympus OM-D E-M5 Mark II",
        ["EM5"] = "Olympus OM-D E-M5", ["EM10MARKIV"] = "Olympus OM-D E-M10 Mark IV",
        ["EM10M4"] = "Olympus OM-D E-M10 Mark IV", ["EM10MARKIII"] = "Olympus OM-D E-M10 Mark III",
        ["EM10M2"] = "Olympus OM-D E-M10 Mark II", ["EM10"] = "Olympus OM-D E-M10",
        ["PENF"] = "Olympus PEN-F", ["EP7"] = "Olympus PEN E-P7", ["EPL10"] = "Olympus PEN E-PL10",
        ["EPL9"] = "Olympus PEN E-PL9", ["TG6"] = "Olympus Tough TG-6", ["TG7"] = "OM System Tough TG-7",
        ["STYLUS1"] = "Olympus Stylus 1",

        // ---------------- Ricoh / Pentax ----------------
        ["GRIV"] = "Ricoh GR IV", ["GRIIIX"] = "Ricoh GR IIIx", ["GRIII"] = "Ricoh GR III",
        ["GRII"] = "Ricoh GR II", ["GR"] = "Ricoh GR", ["G900"] = "Ricoh G900",
        ["THETA Z1"] = "Ricoh THETA Z1", ["THETAZ1"] = "Ricoh THETA Z1", ["THETAX"] = "Ricoh THETA X",
        ["THETAV"] = "Ricoh THETA V", ["THETAS"] = "Ricoh THETA S",
        ["K3MARKIII"] = "Pentax K-3 Mark III", ["K3M3"] = "Pentax K-3 Mark III",
        ["K1MARKII"] = "Pentax K-1 Mark II", ["K1M2"] = "Pentax K-1 Mark II", ["K1"] = "Pentax K-1",
        ["K70"] = "Pentax K-70", ["KP"] = "Pentax KP", ["K3"] = "Pentax K-3", ["K5"] = "Pentax K-5",
        ["645Z"] = "Pentax 645Z",

        // ---------------- 其它常见 ----------------
        ["X2D100C"] = "Hasselblad X2D 100C", ["X1DII50C"] = "Hasselblad X1D II 50C",
        ["907X50C"] = "Hasselblad 907X 50C", ["H6D100C"] = "Hasselblad H6D-100c",
        ["FP"] = "Sigma fp", ["FPL"] = "Sigma fp L",
        ["PIXEL9PRO"] = "Google Pixel 9 Pro", ["PIXEL9"] = "Google Pixel 9",
        ["PIXEL8PRO"] = "Google Pixel 8 Pro", ["PIXEL8"] = "Google Pixel 8",
        ["PIXEL7PRO"] = "Google Pixel 7 Pro", ["PIXEL7"] = "Google Pixel 7",
        ["POCKETCINEMA4K"] = "Blackmagic Pocket Cinema Camera 4K",
        ["POCKETCINEMA6K"] = "Blackmagic Pocket Cinema Camera 6K",
        ["URSA MINI PRO 12K"] = "Blackmagic URSA Mini Pro 12K",
        ["V-RAPTOR 8K VV"] = "RED V-RAPTOR 8K VV",
        ["ALEXA MINI LF"] = "ARRI ALEXA Mini LF",
    };

    private static readonly Dictionary<string, string> MakeCanonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SONY"] = "Sony",
        ["CANON"] = "Canon",
        ["NIKONCORPORATION"] = "Nikon", ["NIKON"] = "Nikon",
        ["FUJIFILM"] = "Fujifilm", ["FUJIFILMINC"] = "Fujifilm",
        ["PANASONIC"] = "Panasonic",
        ["OLYMPUSIMAGINGCORP"] = "Olympus", ["OLYMPUSCORPORATION"] = "Olympus",
        ["OMDIGITALSOLUTIONS"] = "OM System", ["OMSYSTEM"] = "OM System",
        ["RICOHIMAGINGCOMPANYLIMITED"] = "Ricoh", ["RICOHIMAGINGCOMPANY"] = "Ricoh", ["RICOH"] = "Ricoh",
        ["PENTAXRICOHIMAGINGCOMPANYLIMITED"] = "Pentax", ["PENTAX"] = "Pentax",
        ["LEICACAMERAAG"] = "Leica", ["LEICA"] = "Leica",
        ["HASSELBLAD"] = "Hasselblad", ["HASSELBLADAB"] = "Hasselblad",
        ["PHASEONE"] = "Phase One",
        ["SIGMA"] = "Sigma", ["SIGMACORPORATION"] = "Sigma",
        ["SAMSUNG"] = "Samsung", ["SAMSUNGELECTRONICS"] = "Samsung",
        ["GOOGLE"] = "Google",
        ["XIAOMI"] = "Xiaomi", ["HUAWEI"] = "Huawei", ["ONEPLUS"] = "OnePlus",
        ["OPPO"] = "OPPO", ["VIVO"] = "vivo", ["MOTOROLA"] = "Motorola", ["NOTHING"] = "Nothing",
        ["APPLE"] = "Apple", ["DJI"] = "DJI", ["GOPRO"] = "GoPro", ["INSTA360"] = "Insta360",
        ["BLACKMAGICDESIGN"] = "Blackmagic Design", ["RED"] = "RED", ["ARRI"] = "ARRI",
        ["SONYERICSSON"] = "Sony Ericsson",
    };

    // ============================================================ 公共 API

    /// <summary>规范化厂商名：SONY → Sony，NIKON CORPORATION → Nikon；厂商缺失时从机型推断。</summary>
    public static string? NormalizeMake(string? make, string? model = null)
    {
        var cleaned = MetaUtil.CleanMake(make);
        if (cleaned is not null)
        {
            var key = NormalizeKey(cleaned);
            if (MakeCanonical.TryGetValue(key, out var canonical)) return canonical;
            return cleaned;
        }

        if (string.IsNullOrWhiteSpace(model)) return null;
        var modelKey = NormalizeKey(model);
        foreach (var (brandKey, brandName) in MakeCanonical)
        {
            if (modelKey.StartsWith(brandKey, StringComparison.OrdinalIgnoreCase)) return brandName;
        }
        if (modelKey.StartsWith("IPHONE", StringComparison.OrdinalIgnoreCase) ||
            modelKey.StartsWith("IPAD", StringComparison.OrdinalIgnoreCase)) return "Apple";
        if (modelKey.StartsWith("ILCE", StringComparison.OrdinalIgnoreCase) ||
            modelKey.StartsWith("DSCRX", StringComparison.OrdinalIgnoreCase) ||
            modelKey.StartsWith("NEX", StringComparison.OrdinalIgnoreCase)) return "Sony";
        if (modelKey.StartsWith("EOS", StringComparison.OrdinalIgnoreCase) ||
            modelKey.StartsWith("POWERSHOT", StringComparison.OrdinalIgnoreCase)) return "Canon";
        if (modelKey.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase)) return "Nikon";
        if (modelKey.StartsWith("FUJIFILM", StringComparison.OrdinalIgnoreCase)) return "Fujifilm";
        if (modelKey.StartsWith("OM", StringComparison.OrdinalIgnoreCase) && modelKey.Length > 2) return "OM System";
        if (modelKey.StartsWith("FC", StringComparison.OrdinalIgnoreCase) && modelKey.Length == 6) return "DJI";
        if (modelKey.StartsWith("HERO", StringComparison.OrdinalIgnoreCase) ||
            modelKey.StartsWith("GOPRO", StringComparison.OrdinalIgnoreCase)) return "GoPro";
        if (modelKey.StartsWith("INSTA", StringComparison.OrdinalIgnoreCase)) return "Insta360";
        return null;
    }

    /// <summary>设备归一化键（人读名）。未知时返回 "Make Model" 清洗结果，两者都空返回 null。</summary>
    public static string? NormalizeDevice(string? make, string? model, string? fileName = null)
    {
        var canonicalMake = NormalizeMake(make, model);
        var rawModel = MetaUtil.CleanText(model, 128);

        if (!string.IsNullOrWhiteSpace(rawModel))
        {
            var stripped = StripBrand(rawModel);
            var key = NormalizeKey(stripped);
            if (ModelMap.TryGetValue(key, out var mapped)) return mapped;

            // 带厂商前缀的整体 key（"NIKON Z 8" 这类没有被 StripBrand 完全剥掉时）
            var fullKey = NormalizeKey(rawModel);
            if (ModelMap.TryGetValue(fullKey, out var mapped2)) return mapped2;

            // Nikon Z 6_2 / Z 7_3 这种下划线世代写法
            var zKey = NormalizeNikonKey(key);
            if (zKey is not null && ModelMap.TryGetValue(zKey, out var mapped3)) return mapped3;

            // 常见机型族兜底：Canon EOS xxx / Nikon Dxxx / Sony Axxxx
            var family = TryFamilyName(canonicalMake, stripped);
            if (family is not null) return family;

            if (canonicalMake is not null)
            {
                if (stripped.StartsWith(canonicalMake, StringComparison.OrdinalIgnoreCase)) return stripped;
                return $"{canonicalMake} {TitleModel(stripped)}";
            }
            return TitleModel(stripped);
        }

        if (canonicalMake is not null) return canonicalMake;

        // 完全没有元数据时，从文件名兜底推断设备族（DJI_0001 / GOPR0001 / Insta360_…）
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var name = Path.GetFileName(fileName).ToUpperInvariant();
            if (name.StartsWith("DJI_") || name.StartsWith("DJIM")) return "DJI";
            if (name.StartsWith("GOPR") || name.StartsWith("GOPRO") || name.StartsWith("GH0") || name.StartsWith("GX0"))
                return "GoPro";
            if (name.StartsWith("INSTA") || name.StartsWith("INSTA360")) return "Insta360";
            if (name.StartsWith("PXL_")) return "Google Pixel";
            if (name.StartsWith("SAM_")) return "Samsung";
            if (name.StartsWith("MVI_")) return "Canon";
            if (name.StartsWith("DSC_")) return "Nikon";
        }
        return null;
    }

    /// <summary>
    /// 镜头名归一化：清洗控制字符/重复空格、剔除 "----" / "Unknown" 之类占位值。
    /// 厂商命名差异太大（FE 24-70mm F2.8 GM II / RF24-105mm F4 L IS USM / NIKKOR Z 24-70mm f/2.8 S），
    /// 因此保留 EXIF 原始写法，只做安全清洗，避免"归一化"反而破坏用户熟悉的镜头名。
    /// </summary>
    public static string? NormalizeLens(string? lensModel, string? make = null)
    {
        var cleaned = MetaUtil.CleanText(lensModel, 256);
        if (cleaned is null) return null;
        if (cleaned is "----" or "---" or "0" or "Unknown" or "unknown" or "n/a" or "N/A" or "None" or "none")
            return null;

        // 去掉厂商名重复（"Sony Sony FE ..."）
        var canonicalMake = NormalizeMake(make);
        if (canonicalMake is not null)
        {
            var prefix = canonicalMake + " " + canonicalMake + " ";
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[canonicalMake.Length..].TrimStart();
        }
        return MetaUtil.CleanText(cleaned, 256);
    }

    // ============================================================ 内部工具

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>剥掉机型字符串开头的厂商名（"NIKON Z 8" → "Z 8"）。</summary>
    private static string StripBrand(string model)
    {
        var key = NormalizeKey(model);
        foreach (var brand in new[]
                 {
                     "NIKONCORPORATION", "FUJIFILMINC", "OLYMPUSIMAGINGCORP", "OMDIGITALSOLUTIONS",
                     "RICOHIMAGINGCOMPANYLIMITED", "PENTAXRICOHIMAGINGCOMPANYLIMITED", "LEICACAMERAAG",
                     "SIGMACORPORATION", "SAMSUNGELECTRONICS", "BLACKMAGICDESIGN",
                     "NIKON", "FUJIFILM", "OLYMPUS", "PANASONIC", "CANON", "SONY", "PENTAX",
                     "RICOH", "LEICA", "SIGMA", "SAMSUNG", "DJI", "GOPRO", "INSTA360", "APPLE", "OMSYSTEM",
                 })
        {
            if (!key.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) continue;
            var remaining = key[brand.Length..];
            if (remaining.Length == 0) return model;
            // 找到原字符串中 brand 结尾对应的位置（按规格化索引反推过于复杂，直接按字符扫描）
            var idx = 0;
            var seen = 0;
            while (idx < model.Length && seen < brand.Length)
            {
                if (char.IsLetterOrDigit(model[idx])) seen++;
                idx++;
            }
            var tail = model[idx..].TrimStart(' ', '-', '_', '.', ',');
            if (tail.Length > 0) return tail;
        }
        return model;
    }

    /// <summary>"Z 6_2" 规格化后是 Z62，直接映射；这个函数处理 "Z6_2"→"Z62" 之外带世代后缀的其它写法。</summary>
    private static string? NormalizeNikonKey(string key)
    {
        if (key.Length < 3 || key[0] != 'Z') return null;
        // Z6_3 → Z63 已由 NormalizeKey 处理（下划线被移除），无需额外操作
        return key;
    }

    /// <summary>机型族兜底（Canon EOS / Nikon D / Sony A 等常见命名）。</summary>
    private static string? TryFamilyName(string? canonicalMake, string strippedModel)
    {
        if (canonicalMake is null) return null;
        var key = NormalizeKey(strippedModel);
        switch (canonicalMake)
        {
            case "Canon" when key.StartsWith("EOS", StringComparison.OrdinalIgnoreCase):
                return "Canon " + TitleModel(strippedModel);
            case "Nikon" when key.Length is >= 2 and <= 6 && key[0] == 'D' && key.Skip(1).All(char.IsDigit):
                return "Nikon " + strippedModel.ToUpperInvariant();
            case "Sony" when key.StartsWith("ILCE", StringComparison.OrdinalIgnoreCase):
                return "Sony " + strippedModel.ToUpperInvariant();
            case "Sony" when key.StartsWith("A", StringComparison.OrdinalIgnoreCase) && key.Length <= 8:
                return "Sony " + strippedModel.ToUpperInvariant();
            case "Fujifilm" when key.Length >= 3:
                return "Fujifilm " + strippedModel.ToUpperInvariant();
            case "Panasonic" when key.StartsWith("DC", StringComparison.OrdinalIgnoreCase):
                return "Panasonic " + TitleModel(strippedModel);
            case "Leica":
                return "Leica " + TitleModel(strippedModel);
            case "DJI":
                return "DJI " + strippedModel.ToUpperInvariant();
            case "GoPro":
                return "GoPro " + TitleModel(strippedModel);
            case "Insta360":
                return "Insta360 " + TitleModel(strippedModel);
            case "Apple":
                return "Apple " + TitleModel(strippedModel);
            case "OM System":
            case "Olympus":
                return canonicalMake + " " + TitleModel(strippedModel);
            case "Ricoh":
            case "Pentax":
                return canonicalMake + " " + TitleModel(strippedModel);
        }
        return null;
    }

    /// <summary>把全大写的型号改成标题/保留混合大小写（"ILCE-7M4" 保留，全大写 "EOS R5" 转 "EOS R5"）。</summary>
    private static string TitleModel(string model)
    {
        model = model.Trim();
        if (model.Length == 0) return model;
        // 已经含小写字母 → 原样（"iPhone 15 Pro" / "X-T5"）
        if (model.Any(char.IsLower)) return model;

        var words = model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length <= 4 && w.All(c => char.IsDigit(c) || c is '-' or '/' or '.')) continue; // 纯数字/代号保留
            if (w is "EOS" or "OM" or "DJI" or "GPS" or "II" or "III" or "IV" or "V" or "VI" or "X" or "S" or "R" or "Z")
                continue;
            words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }
}