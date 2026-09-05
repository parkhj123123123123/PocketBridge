import CryptoKit
import Foundation
import XCTest
import ZIPFoundation
@testable import PocketBridge

final class FilePreparationTests: XCTestCase {
    private func staged(name: String, data: Data) throws -> StagedFile {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent(name)
        try data.write(to: source)
        return try FileStager.stage(source, securityScoped: false)
    }

    func testCompressesDocumentAndPreservesOriginalHash() async throws {
        let data = Data(String(repeating: "PocketBridge,문서,테스트\n", count: 15000).utf8)
        let file = try staged(name: "데이터.csv", data: data)
        defer { FileStager.remove(file) }
        let prepared = try await FilePreparation.prepare(file, compression: true, phase: { _ in })
        XCTAssertTrue(prepared.compressed)
        XCTAssertLessThan(prepared.payloadSize, file.size * 95 / 100)
        XCTAssertEqual(prepared.sha256, SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined())
        let archive = try Archive(url: prepared.payloadURL, accessMode: .read)
        XCTAssertEqual(Array(archive).count, 1)
        let entry = try XCTUnwrap(archive["content"])
        var unpacked = Data()
        _ = try archive.extract(entry, bufferSize: PacketCodec.chunkSize) { unpacked.append($0) }
        XCTAssertEqual(unpacked, data)
    }

    func testSkipsAlreadyCompressedTypesAndCompressionToggle() async throws {
        let data = Data(repeating: 65, count: 16000)
        for name in ["photo.heic", "movie.mov", "sheet.xlsx", "document.pdf", "bundle.zip"] {
            let file = try staged(name: name, data: data)
            defer { FileStager.remove(file) }
            let prepared = try await FilePreparation.prepare(file, compression: true, phase: { _ in })
            XCTAssertFalse(prepared.compressed, name)
        }
        let file = try staged(name: "plain.txt", data: data)
        defer { FileStager.remove(file) }
        let prepared = try await FilePreparation.prepare(file, compression: false, phase: { _ in })
        XCTAssertFalse(prepared.compressed)
    }

    func testZeroByteFileIsValidAndNotCompressed() async throws {
        let file = try staged(name: "empty.txt", data: Data())
        defer { FileStager.remove(file) }
        let prepared = try await FilePreparation.prepare(file, compression: true, phase: { _ in })
        XCTAssertEqual(prepared.payloadSize, 0)
        XCTAssertFalse(prepared.compressed)
        XCTAssertEqual(prepared.sha256, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
    }
}
