import Foundation
import XCTest
@testable import STJCore

final class AITaggerTests: XCTestCase {
    private func tagger() -> AITagger {
        AITagger(configuration: .init(endpoint: "https://example.com/v1", model: "mock-vision-1", apiKey: "test-key"))
    }

    private func response(content: String) -> Data {
        let payload: [String: Any] = [
            "id": "chatcmpl-1",
            "choices": [["index": 0, "message": ["role": "assistant", "content": content]]],
        ]
        return try! JSONSerialization.data(withJSONObject: payload)
    }

    func testChatCompletionURLNormalizes() {
        XCTAssertEqual(AITagger.chatCompletionURL(endpoint: "https://api.openai.com/v1")?.absoluteString,
                       "https://api.openai.com/v1/chat/completions")
        XCTAssertEqual(AITagger.chatCompletionURL(endpoint: "https://x.test/v1/")?.absoluteString,
                       "https://x.test/v1/chat/completions")
        XCTAssertEqual(AITagger.chatCompletionURL(endpoint: "https://x.test/v1/chat/completions")?.absoluteString,
                       "https://x.test/v1/chat/completions")
        XCTAssertNil(AITagger.chatCompletionURL(endpoint: ""))
    }

    func testRequestBodyContainsModelPromptAndImage() async throws {
        let payload = await tagger().makeRequestBody(imageData: Data([0xFF, 0xD8, 0xFF, 0x00]))
        let body = try XCTUnwrap(payload)
        let object = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertEqual(object["model"] as? String, "mock-vision-1")
        let messages = try XCTUnwrap(object["messages"] as? [[String: Any]])
        XCTAssertEqual(messages.count, 2)
        let userContent = try XCTUnwrap(messages[1]["content"] as? [[String: Any]])
        let imagePart = try XCTUnwrap(userContent.first { $0["type"] as? String == "image_url" })
        let imageURL = try XCTUnwrap((imagePart["image_url"] as? [String: Any])?["url"] as? String)
        XCTAssertTrue(imageURL.hasPrefix("data:image/jpeg;base64,"))
        let systemPrompt = try XCTUnwrap(messages[0]["content"] as? String)
        XCTAssertTrue(systemPrompt.contains("theme"))
        XCTAssertTrue(systemPrompt.contains("keywords"))
    }

    func testParseFencedJSONWithAliases() {
        let content = """
        这是结果：
        ```json
        {"主题":"city","影调":"tealorange","keywords":["城市","黄昏","城市"],
         "描述":"黄昏时分的城市天际线","情绪":"温暖","is_log":false,"quality":0.83,"confidence":0.91}
        ```
        """
        let suggestion = AITagger.parse(content: content)
        XCTAssertNil(suggestion.error)
        XCTAssertEqual(suggestion.theme, "city")
        XCTAssertEqual(suggestion.tone, "tealorange")
        XCTAssertEqual(suggestion.keywords, ["城市", "黄昏"])
        XCTAssertEqual(suggestion.subject, "黄昏时分的城市天际线")
        XCTAssertEqual(suggestion.mood, "温暖")
        XCTAssertEqual(suggestion.isLog, false)
        XCTAssertEqual(suggestion.qualityScore ?? 0, 0.83, accuracy: 0.001)
        XCTAssertEqual(suggestion.confidence, 0.91, accuracy: 0.001)
    }

    func testParseRejectsMissingJSONAndEmptyPayload() {
        XCTAssertNotNil(AITagger.parse(content: "抱歉，我无法处理这张图片").error)
        XCTAssertNotNil(AITagger.parse(content: "{\"something\": 1}").error)
    }

    func testExtractContentFromOpenAIResponse() {
        let data = response(content: "{\"theme\":\"night\"}")
        XCTAssertEqual(AITagger.extractContent(from: data), "{\"theme\":\"night\"}")
    }

    func testPatchMergesKeywordsAndKeepsManualTags() {
        let asset = Asset(id: "a1", path: "/tmp/a.jpg", fileName: "a.jpg",
                          keywords: ["陆家嘴", "夜幕"], themes: ["street"], tone: "cool")
        let suggestion = AISuggestion(theme: "city", tone: "tealorange",
                                      keywords: ["陆家嘴", "新词", "城市"],
                                      subject: "夜景", mood: "神秘", isLog: false,
                                      qualityScore: 0.9, confidence: 0.9)
        let patch = AITagger.patch(for: asset, suggestion: suggestion)
        guard case .set(let themes) = patch.themes else { return XCTFail("themes 应被写入") }
        XCTAssertEqual(themes.first, "city")
        XCTAssertTrue(themes.contains("street"))
        guard case .set(let keywords) = patch.keywords else { return XCTFail("keywords 应被写入") }
        XCTAssertEqual(keywords, ["陆家嘴", "夜幕", "新词", "城市"])
        XCTAssertEqual(patch.qualityScore ?? 0, 0.9, accuracy: 0.001)
        guard case .set(let subject) = patch.aiSubject else { return XCTFail("aiSubject 应写入") }
        XCTAssertEqual(subject, "夜景")
    }

    func testPatchSkipsIsLogWhenMetadataAlreadyKnown() {
        let asset = Asset(id: "a2", path: "/tmp/b.mov", fileName: "b.mov", kind: 2, isLog: true)
        let suggestion = AISuggestion(isLog: false)
        let patch = AITagger.patch(for: asset, suggestion: suggestion)
        XCTAssertTrue(patch.isLog.isUnchanged)
    }
}
