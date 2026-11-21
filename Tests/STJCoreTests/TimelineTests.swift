import Foundation
import XCTest
@testable import STJCore

final class TimelineTests: XCTestCase {
    private func asset(id: String, day: String?, modified: Date? = nil) -> Asset {
        Asset(id: id,
              path: "/tmp/\(id).jpg",
              fileName: "\(id).jpg",
              fileModifiedAt: modified,
              capturedDay: day)
    }

    func testSectionsGroupByDayDescendingWithUndatedLast() {
        let assets = [
            asset(id: "a", day: "2026-08-19"),
            asset(id: "b", day: "2024-02-11"),
            asset(id: "c", day: "2026-08-19"),
            asset(id: "d", day: nil),
        ]
        let sections = Timeline.sections(from: assets)
        XCTAssertEqual(sections.map(\.day), ["2026-08-19", "2024-02-11", Timeline.undatedKey])
        XCTAssertEqual(sections[0].assets.map(\.id), ["a", "c"])
        XCTAssertEqual(sections[0].count, 2)
        XCTAssertEqual(sections[2].assets.map(\.id), ["d"])
    }

    func testDayKeyFallsBackToFileModifiedDate() {
        let date = ISO8601DateFormatter().date(from: "2025-06-30T10:00:00Z")!
        let fallback = asset(id: "x", day: nil, modified: date)
        let withDay = asset(id: "y", day: "2026-08-19")
        XCTAssertFalse(Timeline.dayKey(for: fallback).isEmpty)
        XCTAssertNotEqual(Timeline.dayKey(for: fallback), Timeline.undatedKey)
        XCTAssertEqual(Timeline.dayKey(for: withDay), "2026-08-19")
        // 既没有拍摄日也没有修改时间 → 未标注日期
        XCTAssertEqual(Timeline.dayKey(for: asset(id: "z", day: nil)), Timeline.undatedKey)
    }

    func testSectionTitleIsLocalized() {
        let title = Timeline.title(for: "2026-08-19")
        XCTAssertTrue(title.contains("2026"), title)
        XCTAssertTrue(title.contains("8 月"), title)
        XCTAssertTrue(title.contains("19"), title)
        XCTAssertEqual(Timeline.title(for: Timeline.undatedKey), Timeline.undatedKey)
    }
}
