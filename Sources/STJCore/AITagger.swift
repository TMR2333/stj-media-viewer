import AppKit
import Foundation

/// AI 分类建议：字段对齐 Windows 端 `AiJsonParser`（theme/tone/keywords/subject/mood/is_log/quality/confidence）。
public struct AISuggestion: Sendable, Equatable {
    public var theme: String?
    public var tone: String?
    public var keywords: [String]
    public var subject: String?
    public var mood: String?
    public var isLog: Bool?
    public var qualityScore: Double?
    public var confidence: Double
    public var error: String?

    public init(theme: String? = nil, tone: String? = nil, keywords: [String] = [],
                subject: String? = nil, mood: String? = nil, isLog: Bool? = nil,
                qualityScore: Double? = nil, confidence: Double = 0.7, error: String? = nil) {
        self.theme = theme
        self.tone = tone
        self.keywords = keywords
        self.subject = subject
        self.mood = mood
        self.isLog = isLog
        self.qualityScore = qualityScore
        self.confidence = confidence
        self.error = error
    }

    public var hasContent: Bool {
        theme != nil || tone != nil || subject != nil || mood != nil
            || !keywords.isEmpty || isLog != nil || qualityScore != nil
    }
}

/// AI 分类客户端：OpenAI 兼容 `/chat/completions`，图片以 data URI 发送。
public actor AITagger {
    public struct Configuration: Sendable {
        public var endpoint: String
        public var model: String
        public var apiKey: String
        public var timeout: TimeInterval
        /// 发送前把图片缩到最长边这么多像素，避免请求体过大。
        public var maxImageEdge: CGFloat

        public init(endpoint: String, model: String, apiKey: String,
                    timeout: TimeInterval = 120, maxImageEdge: CGFloat = 1024) {
            self.endpoint = endpoint
            self.model = model
            self.apiKey = apiKey
            self.timeout = timeout
            self.maxImageEdge = maxImageEdge
        }
    }

    public enum AIError: LocalizedError {
        case notConfigured
        case badEndpoint
        case httpStatus(Int, String)
        case emptyResponse
        case transport(String)

        public var errorDescription: String? {
            switch self {
            case .notConfigured: return "没有配置 AI 密钥（设置 → AI）"
            case .badEndpoint: return "AI 接口地址无效"
            case .httpStatus(let code, let body): return "AI 接口返回 HTTP \(code)：\(body.prefix(160))"
            case .emptyResponse: return "AI 返回内容为空"
            case .transport(let message): return "AI 请求失败：\(message)"
            }
        }
    }

    private let configuration: Configuration
    private let session: URLSession

    public init(configuration: Configuration, session: URLSession? = nil) {
        self.configuration = configuration
        if let session {
            self.session = session
        } else {
            let config = URLSessionConfiguration.ephemeral
            config.timeoutIntervalForRequest = configuration.timeout
            config.timeoutIntervalForResource = configuration.timeout * 2
            self.session = URLSession(configuration: config)
        }
    }

    /// 系统提示词：与 Windows 端 PromptLibrary 保持同样的输出契约。
    public static let systemPrompt = """
    你是摄影素材分类助手。请阅读图片，只返回一个 JSON 对象，不要任何解释或 Markdown。
    JSON 字段：
      "theme": 主题 ID，取值 nature/humanities/city/street/portrait/candid/art/architecture/stilllife/landscape/wildlife/night/travel/sport/macro/documentary/product/food/other
      "tone": 影调 ID，取值 highkey/lowkey/midkey/hard/soft/cool/warm/mono/lowsat/hisat/tealorange/vintage/cinematic/neon/natural/other
      "keywords": 3~8 个中文关键词或专有名词，不要重复 theme/tone 的显示名
      "subject": 一句话描述画面主体（不超过 60 字）
      "mood": 情绪词（如 平静/温暖/孤寂/热烈/神秘/轻盈/怀旧/克制/辽阔/喧闹）
      "is_log": 是否 Log 视频画面（true/false）
      "quality": 画面技术质量 0~1 的小数
      "confidence": 你的置信度 0~1 的小数
    """

    /// 请求体（便于测试断言）。
    public func makeRequestBody(imageData: Data, mimeType: String = "image/jpeg") -> Data? {
        let payload: [String: Any] = [
            "model": configuration.model,
            "temperature": 0.2,
            "max_tokens": 600,
            "response_format": ["type": "json_object"],
            "messages": [
                ["role": "system", "content": Self.systemPrompt],
                ["role": "user", "content": [
                    ["type": "text", "text": "请分类这张图片，只输出 JSON。"],
                    ["type": "image_url", "image_url": ["url": "data:\(mimeType);base64,\(imageData.base64EncodedString())"]],
                ]],
            ],
        ]
        return try? JSONSerialization.data(withJSONObject: payload)
    }

    /// 调一次模型，返回解析后的建议（解析失败时 `error` 非空）。
    public func suggest(imageData: Data, mimeType: String = "image/jpeg") async throws -> AISuggestion {
        guard !configuration.apiKey.isEmpty else { throw AIError.notConfigured }
        guard let url = Self.chatCompletionURL(endpoint: configuration.endpoint) else { throw AIError.badEndpoint }
        guard let body = makeRequestBody(imageData: imageData, mimeType: mimeType) else {
            throw AIError.transport("无法构造请求体")
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(configuration.apiKey)", forHTTPHeaderField: "Authorization")
        request.httpBody = body

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw AIError.transport(error.localizedDescription)
        }
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            let text = String(data: data, encoding: .utf8) ?? ""
            throw AIError.httpStatus(http.statusCode, text)
        }
        guard let content = Self.extractContent(from: data), !content.isEmpty else {
            throw AIError.emptyResponse
        }
        return Self.parse(content: content)
    }

    // MARK: - 解析

    static func chatCompletionURL(endpoint: String) -> URL? {
        var base = endpoint.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty else { return nil }
        while base.hasSuffix("/") { base.removeLast() }
        if !base.hasSuffix("/chat/completions") { base += "/chat/completions" }
        return URL(string: base)
    }

    /// 从 OpenAI 兼容响应里取 `choices[0].message.content`。
    public static func extractContent(from data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = object["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any] else { return nil }
        if let content = message["content"] as? String { return content }
        if let parts = message["content"] as? [[String: Any]] {
            let texts = parts.compactMap { $0["text"] as? String }
            return texts.isEmpty ? nil : texts.joined()
        }
        return nil
    }

    /// 解析模型输出：容忍 \`\`\`json 代码块与前后说明文字。
    public static func parse(content: String) -> AISuggestion {
        guard let jsonText = extractJSONObject(from: content) else {
            return AISuggestion(error: "未能在模型返回中找到合法 JSON")
        }
        guard let data = jsonText.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return AISuggestion(error: "模型返回的 JSON 解析失败")
        }
        func string(_ keys: String...) -> String? {
            for key in keys {
                if let value = object[key] as? String {
                    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !trimmed.isEmpty { return trimmed }
                }
            }
            return nil
        }
        func double(_ keys: String...) -> Double? {
            for key in keys {
                if let value = object[key] as? Double { return value }
                if let value = object[key] as? Int { return Double(value) }
                if let value = object[key] as? String, let parsed = Double(value) { return parsed }
            }
            return nil
        }
        func boolean(_ keys: String...) -> Bool? {
            for key in keys {
                if let value = object[key] as? Bool { return value }
                if let value = object[key] as? Int { return value != 0 }
                if let value = object[key] as? String {
                    let lowered = value.lowercased()
                    if ["true", "1", "yes"].contains(lowered) { return true }
                    if ["false", "0", "no"].contains(lowered) { return false }
                }
            }
            return nil
        }
        var keywords: [String] = []
        if let raw = object["keywords"] as? [Any] {
            keywords = raw.compactMap { $0 as? String }
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
        }
        var seen = Set<String>()
        keywords = keywords.filter { keyword in
            let key = keyword.lowercased()
            if seen.contains(key) { return false }
            seen.insert(key)
            return true
        }

        let suggestion = AISuggestion(
            theme: string("theme", "themeId", "theme_id", "category", "topic", "主题"),
            tone: string("tone", "toneId", "tone_id", "colorTone", "影调", "色调"),
            keywords: Array(keywords.prefix(10)),
            subject: string("subject", "description", "caption", "content", "mainSubject", "主体", "描述"),
            mood: string("mood", "feeling", "atmosphere", "氛围", "情绪"),
            isLog: boolean("is_log", "isLog", "log"),
            qualityScore: double("quality", "qualityScore", "quality_score", "score", "质量"),
            confidence: double("confidence", "conf", "置信度") ?? 0.7
        )
        return suggestion.hasContent ? suggestion : AISuggestion(error: "模型返回的 JSON 缺少有效分类字段")
    }

    /// 从任意文本里提取第一个平衡的 JSON 对象（支持 ```json 围栏）。
    static func extractJSONObject(from text: String) -> String? {
        var candidate = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let fence = candidate.range(of: "```") {
            let afterFence = candidate[fence.upperBound...]
            var body = afterFence
            if let newline = body.firstIndex(of: "\n") {
                body = body[body.index(after: newline)...]
            }
            if let end = body.range(of: "```") {
                body = body[..<end.lowerBound]
            }
            candidate = String(body).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let start = candidate.firstIndex(of: "{") {
            var depth = 0
            var inString = false
            var escaped = false
            var index = start
            while index < candidate.endIndex {
                let character = candidate[index]
                if inString {
                    if escaped { escaped = false }
                    else if character == "\\" { escaped = true }
                    else if character == "\"" { inString = false }
                } else if character == "\"" {
                    inString = true
                } else if character == "{" {
                    depth += 1
                } else if character == "}" {
                    depth -= 1
                    if depth == 0 { return String(candidate[start...index]) }
                }
                index = candidate.index(after: index)
            }
        }
        return nil
    }

    // MARK: - 应用

    /// 把建议合成为 `AssetPatch`（只写 AI 字段；关键词做并集，保留人工标签）。
    public static func patch(for asset: Asset, suggestion: AISuggestion) -> AssetPatch {
        var patch = AssetPatch()
        if let theme = suggestion.theme {
            var themes = asset.themes
            if let index = themes.firstIndex(where: { $0.caseInsensitiveCompare(theme) == .orderedSame }) {
                themes.remove(at: index)
            }
            themes.insert(theme, at: 0)
            patch.themes = .set(themes)
        }
        if let tone = suggestion.tone { patch.tone = .set(tone) }
        if !suggestion.keywords.isEmpty {
            var merged = asset.keywords
            for keyword in suggestion.keywords {
                let trimmed = String(keyword.prefix(40))
                if !merged.contains(where: { $0.caseInsensitiveCompare(trimmed) == .orderedSame }) {
                    merged.append(trimmed)
                }
            }
            patch.keywords = .set(merged)
        }
        if let subject = suggestion.subject { patch.aiSubject = .set(String(subject.prefix(120))) }
        if let mood = suggestion.mood { patch.aiMood = .set(String(mood.prefix(40))) }
        if let isLog = suggestion.isLog, asset.isLog == nil { patch.isLog = .set(isLog) }
        if let quality = suggestion.qualityScore {
            patch.qualityScore = min(max(quality, 0), 1)
        }
        return patch
    }
}

public enum AIProgress {
    /// 生成适合模型输入的小图（JPEG data，最长边 `maxEdge`）。
    public static func previewJPEGData(for asset: Asset, maxEdge: CGFloat = 1024) async -> Data? {
        if !asset.isVideo, let image = ImageLoader.previewImage(at: asset.fileURL, maxPixelSize: Int(maxEdge)) {
            return jpegData(image)
        }
        if let thumbnail = await ThumbnailStore.shared.image(for: asset, targetSize: maxEdge) {
            return jpegData(thumbnail.image)
        }
        return nil
    }

    private static func jpegData(_ image: NSImage) -> Data? {
        guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff) else { return nil }
        return bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.82])
    }
}
