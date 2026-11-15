using System.Text.RegularExpressions;

namespace StjMediaViewer.Core.Classification;

/// <summary>
/// 设备键归一化：把 EXIF 里五花八门的 <c>Make/Model</c>（"NIKON CORPORATION" / "NIKON Z 9" /
/// "ILCE-7M4"）整理成稳定、可筛选的显示键（"Nikon Z 9" / "Sony ILCE-7M4"）。
/// <para>只做本地确定性推断；拿不准时返回 null / 低置信度，不编造。</para>
/// </summary>
public static class DeviceNormalizer
{
    /// <summary>一次设备推断的结果。</summary>
    public sealed record DeviceGuess(string Key, double Confidence, string? Note = null);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingBrandRegex = new(
        @"^(NIKON|CANON|SONY|FUJIFILM|FUJI|PANASONIC|OLYMPUS|LEICA|HASSELBLAD|SIGMA|PENTAX|RICOH|SAMSUNG|APPLE|GOOGLE|XIAOMI|HUAWEI|DJI|GOPRO|INSTA360)\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GenericModelSuffixRegex = new(
        @"\b(DIGITAL CAMERA|DIGITAL STILL CAMERA|CAMERA|STILL CAMERA)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>DJI 相机代码 → 上市名（EXIF Model 常是 FCxxxx）。</summary>
    private static readonly Dictionary<string, string> DjiCameraCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FC220"] = "Mavic Pro",
        ["FC330"] = "Phantom 4",
        ["FC6310"] = "Phantom 4 Pro",
        ["FC6520"] = "Mavic Air 2",
        ["FC3411"] = "Air 2S",
        ["FC7303"] = "Mini 2",
        ["FC3582"] = "Mavic 3",
        ["FC8482"] = "Mini 3 Pro",
        ["FC2103"] = "Mavic Air",
        ["FC3170"] = "Mavic Air",
    };

    /// <summary>归一化设备键；完全无法识别返回 null。</summary>
    public static string? Normalize(string? make, string? model, string? fileName = null) =>
        Guess(make, model, fileName)?.Key;

    /// <summary>归一化设备键并给出置信度与依据。</summary>
    public static DeviceGuess? Guess(string? make, string? model, string? fileName = null)
    {
        try
        {
            var rawMake = Clean(make);
            var rawModel = Clean(model);

            var brand = CanonicalBrand(rawMake) ?? CanonicalBrand(rawModel) ?? BrandFromModel(rawModel);
            var brandFromExplicitMake = CanonicalBrand(rawMake) is not null;

            if (brand is not null)
            {
                var modelText = NormalizeModel(brand, rawModel);

                if (brand == "DJI" && modelText.Length > 0 &&
                    DjiCameraCodes.TryGetValue(modelText, out var djiName))
                {
                    return new DeviceGuess("DJI " + djiName, 0.9, "DJI 相机代码映射");
                }

                if (modelText.Length == 0)
                    return new DeviceGuess(brand, brandFromExplicitMake ? 0.7 : 0.6, "只识别到品牌");

                var key = modelText.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
                    ? modelText
                    : brand + " " + modelText;

                var confidence = brandFromExplicitMake && rawModel.Length > 0 ? 0.95 : 0.8;
                return new DeviceGuess(key, confidence);
            }

            if (rawModel.Length > 0)
                return new DeviceGuess(rawModel, 0.5, "只有机型字段，无法归一化品牌");

            return GuessFromFileName(fileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把品牌别名映射到统一显示名。</summary>
    public static string? CanonicalBrand(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return null;
        var u = s.ToUpperInvariant();

        if (u.Contains("SONY") || u.Contains("索尼")) return "Sony";
        if (u.Contains("CANON") || u.Contains("佳能")) return "Canon";
        if (u.Contains("NIKON") || u.Contains("尼康")) return "Nikon";
        if (u.Contains("FUJIFILM") || u.Contains("FUJI") || u.Contains("富士")) return "Fujifilm";
        if (u.Contains("PANASONIC") || u.Contains("LUMIX") || u.Contains("松下")) return "Panasonic";
        if (u.Contains("OM DIGITAL") || u.Contains("OM SYSTEM")) return "OM System";
        if (u.Contains("OLYMPUS") || u.Contains("奥林巴斯")) return "Olympus";
        if (u.Contains("LEICA") || u.Contains("徕卡")) return "Leica";
        if (u.Contains("HASSELBLAD") || u.Contains("哈苏")) return "Hasselblad";
        if (u.Contains("SIGMA") || u.Contains("适马")) return "Sigma";
        if (u.Contains("PENTAX") || u.Contains("RICOH") || u.Contains("宾得")) return "Pentax";
        if (u.Contains("SAMSUNG") || u.Contains("三星")) return "Samsung";
        if (u.Contains("APPLE") || u.Contains("IPHONE")) return "Apple";
        if (u.Contains("GOOGLE") || u.Contains("PIXEL")) return "Google";
        if (u.Contains("XIAOMI") || u.Contains("REDMI") || u.Contains("小米")) return "Xiaomi";
        if (u.Contains("HUAWEI") || u.Contains("华为")) return "Huawei";
        if (u.Contains("DJI") || u.Contains("大疆")) return "DJI";
        if (u.Contains("GOPRO")) return "GoPro";
        if (u.Contains("INSTA360")) return "Insta360";
        if (u.Contains("BLACKMAGIC") || u.Contains("BMD")) return "Blackmagic";
        if (u.Contains("ARRI")) return "ARRI";
        if (u.Contains("Z CAM") || u.Contains("ZCAM")) return "Z CAM";
        if (u == "RED" || u.StartsWith("RED ", StringComparison.Ordinal)) return "RED";
        return null;
    }

    private static string? BrandFromModel(string model)
    {
        if (model.Length == 0) return null;
        var u = model.ToUpperInvariant();

        if (u.StartsWith("ILCE-") || u.StartsWith("ILCE") || u.StartsWith("ZV-") ||
            u.StartsWith("DSC-") || u.StartsWith("DSC") || u.StartsWith("SLT-") ||
            u.StartsWith("A7") || u.StartsWith("A9") || u.StartsWith("A1"))
            return "Sony";
        if (u.StartsWith("EOS") || u.StartsWith("POWERSHOT") || u.StartsWith("LEGRIA")) return "Canon";
        if (u.StartsWith("NIKON") || Regex.IsMatch(u, @"^Z ?\d")) return "Nikon";
        if (u.StartsWith("X-T") || u.StartsWith("X-H") || u.StartsWith("X-S") ||
            u.StartsWith("X-PRO") || u.StartsWith("X100") || u.StartsWith("GFX"))
            return "Fujifilm";
        if (u.StartsWith("LUMIX") || u.StartsWith("DC-")) return "Panasonic";
        if (u.StartsWith("HERO")) return "GoPro";
        if (u.StartsWith("IPHONE")) return "Apple";
        if (u.StartsWith("PIXEL")) return "Google";
        if (u.StartsWith("FC") && u.Length == 6) return "DJI";
        if (u.StartsWith("OM-")) return "OM System";
        return null;
    }

    private static string NormalizeModel(string brand, string rawModel)
    {
        var model = Clean(rawModel);
        if (model.Length == 0) return "";

        // "NIKON Z 9" → "Z 9"；"Canon EOS R5" → "EOS R5"
        model = LeadingBrandRegex.Replace(model, "").Trim();
        model = GenericModelSuffixRegex.Replace(model, "").Trim();
        model = WhitespaceRegex.Replace(model, " ").Trim();

        // 品牌前缀可能与规范名大小写不同（如 "Sony" 已被去成 "SONY"）。
        if (model.Length > 0 && model.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            model = model[brand.Length..].TrimStart();

        return model.Trim();
    }

    private static DeviceGuess? GuessFromFileName(string? fileName)
    {
        var name = Clean(fileName).ToUpperInvariant();
        if (name.Length == 0) return null;

        if (name.StartsWith("DJI_") || name.StartsWith("DJI-")) return new DeviceGuess("DJI", 0.55, "文件名 DJI_ 前缀");
        if (name.StartsWith("GOPR") || name.StartsWith("GP0") || name.StartsWith("GX0") || name.StartsWith("GL0"))
            return new DeviceGuess("GoPro", 0.6, "GoPro 文件名前缀");
        if (name.StartsWith("PXL_")) return new DeviceGuess("Google Pixel", 0.6, "Pixel 文件名前缀");
        if (name.StartsWith("IMG_E")) return new DeviceGuess("Apple iPhone", 0.45, "iPhone 编辑文件名前缀");
        if (name.StartsWith("MVI_") || name.StartsWith("C000") || name.StartsWith("C001"))
            return new DeviceGuess("Canon", 0.4, "佳能相机/摄像机文件名前缀");
        return null;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Replace('\0', ' ').Trim();
        return WhitespaceRegex.Replace(s, " ").Trim();
    }
}