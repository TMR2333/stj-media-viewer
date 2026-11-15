namespace StjMediaViewer.Core.Models;

public sealed class AiChatMessage
{
    public string Role { get; set; } = "user";   // system | user | assistant
    public string? Text { get; set; }
    public byte[]? ImageBytes { get; set; }
    public string ImageMime { get; set; } = "image/jpeg";
}

public sealed class AiRequest
{
    public string? Model { get; set; }
    public List<AiChatMessage> Messages { get; set; } = new();
    public bool RequireJson { get; set; } = true;
    public string? JsonSchema { get; set; }
    public string? JsonSchemaName { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AiRawResponse
{
    public bool Success { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public TimeSpan Latency { get; set; }
    public int HttpStatus { get; set; }
    public string? RawJson { get; set; }
    public string? Model { get; set; }

    public static AiRawResponse Fail(string error, int status = 0) => new() { Success = false, Error = error, HttpStatus = status };
}

/// <summary>AI 对一条素材的分类建议。</summary>
public sealed class AiSuggestion
{
    public string? Theme { get; set; }        // 稳定 ID
    public string? Tone { get; set; }         // 稳定 ID
    public List<string> Keywords { get; set; } = new();
    public string? Subject { get; set; }      // 一句话内容描述
    public string? Mood { get; set; }
    public bool? IsLog { get; set; }
    public double? QualityScore { get; set; } // 0..1
    public double Confidence { get; set; } = 0.7;
    public string? ModelId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string? RawJson { get; set; }
    public string? Error { get; set; }
    public bool Success => string.IsNullOrEmpty(Error);
}

public sealed class AiBatchOptions
{
    /// <summary>null 表示对传入的全部素材处理。</summary>
    public int MaxItems { get; set; } = 50;
    /// <summary>是否覆盖已有的手动分类。默认 false（人工优先）。</summary>
    public bool OverwriteManual { get; set; }
    /// <summary>只处理缺失主题/影调的素材。</summary>
    public bool OnlyUnclassified { get; set; } = true;
    public bool ApplyKeywords { get; set; } = true;
    public bool ApplyQuality { get; set; } = true;
    public double MinConfidenceToApply { get; set; } = 0.35;
}

public sealed class AiBatchProgress
{
    public int Processed { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? CurrentFile { get; set; }
    public string? Message { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public bool Done { get; set; }
    public AiSuggestion? LastSuggestion { get; set; }
    public string? LastAssetId { get; set; }
}

public sealed class AiConnectionTestResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ModelEcho { get; set; }
    public long LatencyMs { get; set; }
}