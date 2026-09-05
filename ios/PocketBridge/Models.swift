import CryptoKit
import Foundation

enum BridgeError: LocalizedError {
    case invalidInvite, insecureServer, notConnected, invalidPacket, unexpectedAck, timeout
    case fileUnavailable, fileTooLarge, remote(String)

    var errorDescription: String? {
        switch self {
        case .invalidInvite: return "연결 코드가 올바르지 않습니다. Windows에서 새 QR 코드를 만들어 주세요."
        case .insecureServer: return "안전한 HTTPS 중계 서버 주소가 필요합니다."
        case .notConnected: return "Windows와 먼저 연결해 주세요."
        case .invalidPacket: return "암호화된 데이터를 확인할 수 없습니다. 새 QR 코드로 연결해 주세요."
        case .unexpectedAck: return "수신 확인 순서가 올바르지 않습니다. 새로 연결해 주세요."
        case .timeout: return "상대 기기의 응답 시간이 초과되었습니다. 새로 연결해 주세요."
        case .fileUnavailable: return "파일을 읽을 수 없습니다. 파일 앱에서 다운로드 상태와 접근 권한을 확인해 주세요."
        case .fileTooLarge: return "현재 버전은 파일 하나당 최대 100 GiB까지 전송할 수 있습니다."
        case .remote(let message): return message
        }
    }
}

struct PairingInvite: Codable, Equatable, Sendable {
    let version: Int
    let server: String
    let room: String
    let token: String
    let key: String

    static func parse(_ text: String) throws -> PairingInvite {
        guard text.utf8.count <= 8192,
              let data = text.data(using: .utf8),
              let result = try? JSONDecoder().decode(Self.self, from: data),
              result.version == 1,
              result.room.range(of: "^[a-fA-F0-9]{32}$", options: .regularExpression) != nil,
              result.token.range(of: "^[A-Za-z0-9_-]{43}$", options: .regularExpression) != nil,
              let keyData = Data(base64Encoded: result.key), keyData.count == 32 else {
            throw BridgeError.invalidInvite
        }
        _ = try result.socketURL()
        return result
    }

    var displayHost: String {
        guard let components = URLComponents(string: server), let host = components.host else { return server }
        return host + (components.port.map { ":\($0)" } ?? "")
    }

    func socketURL() throws -> URL {
        guard var components = URLComponents(string: server),
              let scheme = components.scheme?.lowercased(), let host = components.host, !host.isEmpty,
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil,
              components.path.isEmpty || components.path == "/",
              components.port.map({ (1...65535).contains($0) }) ?? true else {
            throw BridgeError.invalidInvite
        }
        let loopback = ["localhost", "127.0.0.1", "[::1]", "::1"].contains(host.lowercased())
        guard scheme == "https" || (scheme == "http" && loopback) else { throw BridgeError.insecureServer }
        components.scheme = scheme == "https" ? "wss" : "ws"
        components.path = "/ws/\(room)/sender"
        guard let url = components.url else { throw BridgeError.invalidInvite }
        return url
    }
}

struct TransferManifest: Codable, Sendable {
    let transferId: String
    let fileName: String
    let originalSize: Int64
    let payloadSize: Int64
    let compression: String
    let sha256: String
}

struct TransferEnd: Codable { let transferId: String }
struct TransferAck: Codable, Sendable {
    let kind: String
    let transferId: String
    let fileName: String?
    let message: String?
}

enum PacketCodec {
    static let chunkSize = 256 * 1024
    static let maxFileSize: Int64 = 100 * 1024 * 1024 * 1024

    static func seal(type: UInt8, payload: Data, key: SymmetricKey) throws -> Data {
        var plaintext = Data([type])
        plaintext.append(payload)
        guard let combined = try AES.GCM.seal(plaintext, using: key).combined else { throw BridgeError.invalidPacket }
        var result = Data([1])
        result.append(combined)
        return result
    }

    static func open(_ packet: Data, key: SymmetricKey) throws -> (type: UInt8, payload: Data) {
        guard packet.count >= 31, packet.count <= 1024 * 1024, packet.first == 1 else {
            throw BridgeError.invalidPacket
        }
        let box = try AES.GCM.SealedBox(combined: Data(packet.dropFirst()))
        let plain = try AES.GCM.open(box, using: key)
        guard let type = plain.first else { throw BridgeError.invalidPacket }
        return (type, Data(plain.dropFirst()))
    }
}

struct StagedFile: Identifiable, Sendable {
    let id: UUID
    let url: URL
    let name: String
    let size: Int64
}

struct QueueItem: Identifiable {
    let file: StagedFile
    var id: UUID { file.id }
    var fraction = 0.0
    var detail = "전송 대기"
    var state: State = .waiting
    enum State { case waiting, preparing, sending, verifying, complete, failed }
}

struct PreparedFile: Sendable {
    let source: StagedFile
    let payloadURL: URL
    let payloadSize: Int64
    let sha256: String
    var compressed: Bool { payloadURL != source.url }
}

func byteText(_ bytes: Int64) -> String {
    ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
}
