import CryptoKit
import Foundation
import XCTest
@testable import PocketBridge

final class ProtocolTests: XCTestCase {
    private func invite(server: String = "https://relay.example.org") throws -> String {
        let value = PairingInvite(version: 1, server: server, room: String(repeating: "a", count: 32),
                                  token: String(repeating: "B", count: 43), key: Data(repeating: 7, count: 32).base64EncodedString())
        return String(decoding: try JSONEncoder().encode(value), as: UTF8.self)
    }

    func testInviteBuildsSenderEndpoint() throws {
        let value = try PairingInvite.parse(invite())
        XCTAssertEqual(try value.socketURL().absoluteString, "wss://relay.example.org/ws/\(String(repeating: "a", count: 32))/sender")
        XCTAssertEqual(value.displayHost, "relay.example.org")
    }

    func testInviteRejectsInsecureOrCredentialBearingEndpoints() throws {
        for server in ["http://relay.example.org", "https://user:secret@example.org", "https://example.org/path", "https://example.org?token=x", "file:///tmp/server"] {
            XCTAssertThrowsError(try PairingInvite.parse(invite(server: server)), server)
        }
        XCTAssertNoThrow(try PairingInvite.parse(invite(server: "http://localhost:8080")))
    }

    func testInviteRejectsMalformedKeyAndUnsupportedVersion() throws {
        let text = try invite()
        XCTAssertThrowsError(try PairingInvite.parse(text.replacingOccurrences(of: "\"version\":1", with: "\"version\":2")))
        let value = PairingInvite(version: 1, server: "https://example.org", room: String(repeating: "a", count: 32), token: "short", key: "AA==")
        XCTAssertThrowsError(try PairingInvite.parse(String(decoding: JSONEncoder().encode(value), as: UTF8.self)))
    }

    func testChunkEncryptionAndTamperRejection() throws {
        let key = SymmetricKey(data: Data(repeating: 42, count: 32))
        let data = Data(repeating: 0xEA, count: PacketCodec.chunkSize)
        let packet = try PacketCodec.seal(type: 2, payload: data, key: key)
        XCTAssertEqual(packet.count, data.count + 31)
        let decoded = try PacketCodec.open(packet, key: key)
        XCTAssertEqual(decoded.type, 2)
        XCTAssertEqual(decoded.payload, data)
        var damaged = packet
        damaged[20] ^= 1
        XCTAssertThrowsError(try PacketCodec.open(damaged, key: key))
        XCTAssertThrowsError(try PacketCodec.open(packet, key: SymmetricKey(size: .bits256)))
    }

    func testNonceChangesBetweenPackets() throws {
        let key = SymmetricKey(size: .bits256)
        let first = try PacketCodec.seal(type: 2, payload: Data(), key: key)
        let second = try PacketCodec.seal(type: 2, payload: Data(), key: key)
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(try PacketCodec.open(first, key: key).payload.count, 0)
    }

    func testDotNetCompatibleNISTPacket() throws {
        // Shared with the .NET receiver tests: zero key, zero nonce, sixteen zero plaintext bytes.
        let hex = "01000000000000000000000000cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919"
        let bytes = stride(from: 0, to: hex.count, by: 2).map { offset -> UInt8 in
            let start = hex.index(hex.startIndex, offsetBy: offset)
            let end = hex.index(start, offsetBy: 2)
            return UInt8(hex[start..<end], radix: 16)!
        }
        let decoded = try PacketCodec.open(Data(bytes), key: SymmetricKey(data: Data(repeating: 0, count: 32)))
        XCTAssertEqual(decoded.type, 0)
        XCTAssertEqual(decoded.payload, Data(repeating: 0, count: 15))
    }

    func testManifestMatchesDotNetContract() throws {
        let bytes = Data("{\"transferId\":\"88684b4b-c780-4eb3-a4c9-d88fe7768ffe\",\"fileName\":\"기록.txt\",\"originalSize\":12,\"payloadSize\":12,\"compression\":\"none\",\"sha256\":\"\(String(repeating: "a", count: 64))\"}".utf8)
        let manifest = try JSONDecoder().decode(TransferManifest.self, from: bytes)
        XCTAssertEqual(manifest.originalSize, 12)
        XCTAssertEqual(manifest.fileName, "기록.txt")
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(manifest)) as? [String: Any])
        XCTAssertEqual(Set(object.keys), Set(["transferId", "fileName", "originalSize", "payloadSize", "compression", "sha256"]))
    }
}
