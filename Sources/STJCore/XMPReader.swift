import Foundation

// MARK: - XMP 读取（导入时回填）

/// 从 `.xmp` 侧车读出的可回填字段（只包含 XMP 里真实存在的值）。
public struct XMPMetadata: Sendable {
    public var rating: Int?
    public var colorLabel: String?
    public var city: String?
    public var country: String?
    public var province: String?
    public var title: String?
    public var caption: String?
    public var creator: String?
    public var keywords: [String] = []

    public var isEmpty: Bool {
        rating == nil && colorLabel == nil && city == nil && country == nil
            && province == nil && title == nil && caption == nil
            && creator == nil && keywords.isEmpty
    }

    public init() {}
}

/// 解析 XMP 侧车文件。支持 Lightroom / Bridge / ExifTool 常见写法：
/// `xmp:Rating`、`xmp:Label`、`photoshop:City/Country/State`、`dc:subject`、
/// `dc:title`、`dc:description`、`dc:creator`。
public enum XMPReader {
    /// 找到素材对应的 XMP 侧车：优先数据库里的 `xmp_path`，其次同目录同基名的 `.xmp`/`.XMP`。
    public static func sidecarURL(for asset: Asset, fileManager: FileManager = .default) -> URL? {
        if let path = asset.xmpPath?.trimmingCharacters(in: .whitespacesAndNewlines), !path.isEmpty,
           fileManager.fileExists(atPath: path) {
            return URL(fileURLWithPath: path)
        }
        let base = URL(fileURLWithPath: asset.path).deletingPathExtension()
        for ext in ["xmp", "XMP"] {
            let candidate = base.appendingPathExtension(ext)
            if fileManager.fileExists(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    public static func metadata(forAssetAt path: String, fileManager: FileManager = .default) -> XMPMetadata? {
        metadata(forFileAt: URL(fileURLWithPath: path), fileManager: fileManager)
    }

    public static func metadata(forFileAt fileURL: URL, fileManager: FileManager = .default) -> XMPMetadata? {
        let base = fileURL.deletingPathExtension()
        for ext in ["xmp", "XMP"] {
            let candidate = base.appendingPathExtension(ext)
            if fileManager.fileExists(atPath: candidate.path),
               let data = try? Data(contentsOf: candidate) {
                return parse(data)
            }
        }
        return nil
    }

    public static func metadata(for asset: Asset, fileManager: FileManager = .default) -> XMPMetadata? {
        guard let url = sidecarURL(for: asset, fileManager: fileManager),
              let data = try? Data(contentsOf: url) else { return nil }
        return parse(data)
    }

    public static func parse(_ data: Data) -> XMPMetadata? {
        let delegate = Delegate()
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        guard parser.parse(), !delegate.metadata.isEmpty else { return nil }
        return delegate.metadata
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var metadata = XMPMetadata()
        private var stack: [String] = []
        private var textBuffer = ""
        private var capture: Capture?

        private enum Capture {
            case subject
            case title
            case caption
            case creator
        }

        private func localName(_ name: String) -> String {
            if let index = name.lastIndex(of: ":") {
                return String(name[name.index(after: index)...])
            }
            return name
        }

        func parser(_ parser: XMLParser, didStartElement elementName: String,
                    namespaceURI: String?, qualifiedName qName: String?,
                    attributes attributeDict: [String: String] = [:]) {
            let local = localName(elementName)
            stack.append(local)
            textBuffer = ""

            for (key, value) in attributeDict {
                let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { continue }
                switch localName(key) {
                case "Rating":
                    if let rating = Int(trimmed) { metadata.rating = min(max(rating, 0), 5) }
                case "Label":
                    metadata.colorLabel = trimmed
                case "City":
                    metadata.city = trimmed
                case "Country":
                    metadata.country = trimmed
                case "State":
                    metadata.province = trimmed
                default:
                    break
                }
            }

            switch local {
            case "subject": capture = .subject
            case "title" where capture == nil: capture = .title
            case "description" where capture == nil: capture = .caption
            case "creator" where capture == nil: capture = .creator
            case "li": break
            default: break
            }

            // 也支持 <photoshop:City>上海</photoshop:City> 这种元素写法
            if ["City", "Country", "State"].contains(local) {
                elementTextTarget = local
            }
        }

        private var elementTextTarget: String?

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            textBuffer += string
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String,
                    namespaceURI: String?, qualifiedName qName: String?) {
            let local = localName(elementName)
            let text = textBuffer.trimmingCharacters(in: .whitespacesAndNewlines)

            if let target = elementTextTarget, local == target, !text.isEmpty {
                switch target {
                case "City": metadata.city = text
                case "Country": metadata.country = text
                case "State": metadata.province = text
                default: break
                }
                elementTextTarget = nil
            }

            if local == "li", !text.isEmpty, let capture {
                switch capture {
                case .subject:
                    if !metadata.keywords.contains(text) { metadata.keywords.append(text) }
                case .title:
                    if metadata.title == nil { metadata.title = text }
                case .caption:
                    if metadata.caption == nil { metadata.caption = text }
                case .creator:
                    if metadata.creator == nil { metadata.creator = text }
                }
            }

            switch local {
            case "subject", "title", "description", "creator":
                capture = nil
            default:
                break
            }

            if !stack.isEmpty { stack.removeLast() }
            textBuffer = ""
        }
    }
}