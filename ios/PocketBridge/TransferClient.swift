import CryptoKit
import Foundation

private final class SessionDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    // A QR chooses one host. Never forward its bearer token to a redirected host.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

private final class PingCompletion: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    init(_ continuation: CheckedContinuation<Void, Error>) { self.continuation = continuation }
    func finish(_ error: Error?) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        if let error { pending?.resume(throwing: error) } else { pending?.resume() }
    }
}

actor TransferClient {
    private var socket: URLSessionWebSocketTask?
    private var session: URLSession?
    private var key: SymmetricKey?
    private var sending = false

    func connect(_ invite: PairingInvite) async throws {
        disconnect()
        guard let keyData = Data(base64Encoded: invite.key), keyData.count == 32 else { throw BridgeError.invalidInvite }
        var request = URLRequest(url: try invite.socketURL())
        request.setValue("Bearer \(invite.token)", forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 30
        let config = URLSessionConfiguration.ephemeral
        config.waitsForConnectivity = false
        config.allowsCellularAccess = true
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 12 * 60 * 60
        let newSession = URLSession(configuration: config, delegate: SessionDelegate(), delegateQueue: nil)
        let newSocket = newSession.webSocketTask(with: request)
        newSocket.maximumMessageSize = 1024 * 1024
        session = newSession
        socket = newSocket
        key = SymmetricKey(data: keyData)
        newSocket.resume()
        do {
            try await Self.ping(newSocket)
            try Task.checkCancellation()
            guard socket === newSocket else { throw CancellationError() }
        } catch {
            if socket === newSocket { disconnect() }
            throw error
        }
    }

    func disconnect() {
        socket?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        socket = nil
        session = nil
        key = nil
        sending = false
    }

    func healthCheck() async throws {
        guard let socket else { throw BridgeError.notConnected }
        try await Self.ping(socket)
    }

    func send(_ prepared: PreparedFile,
              progress: @escaping @Sendable (Int64, Int64, Bool) async -> Void) async throws -> TransferAck {
        guard let socket, let key, !sending else { throw BridgeError.notConnected }
        sending = true
        defer { sending = false }
        let transferId = UUID().uuidString
        let manifest = TransferManifest(transferId: transferId, fileName: prepared.source.name,
                                        originalSize: prepared.source.size, payloadSize: prepared.payloadSize,
                                        compression: prepared.compressed ? "zip" : "none", sha256: prepared.sha256)
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await Self.sendPacket(type: 1, payload: JSONEncoder().encode(manifest), socket: socket, key: key)
            _ = try await Self.ack(kind: "ready", transferId: transferId, socket: socket, key: key, timeout: 60)
            let handle = try FileHandle(forReadingFrom: prepared.payloadURL)
            defer { try? handle.close() }
            var sent: Int64 = 0
            var lastUpdate = Date.distantPast
            while true {
                try Task.checkCancellation()
                let chunk = try handle.read(upToCount: PacketCodec.chunkSize) ?? Data()
                if chunk.isEmpty { break }
                try await Self.sendPacket(type: 2, payload: chunk, socket: socket, key: key)
                sent += Int64(chunk.count)
                guard sent <= prepared.payloadSize else { throw BridgeError.fileUnavailable }
                if Date().timeIntervalSince(lastUpdate) >= 0.1 || sent == prepared.payloadSize {
                    await progress(sent, prepared.payloadSize, false)
                    lastUpdate = Date()
                }
            }
            guard sent == prepared.payloadSize else { throw BridgeError.fileUnavailable }
            try await Self.sendPacket(type: 3, payload: JSONEncoder().encode(TransferEnd(transferId: transferId)), socket: socket, key: key)
            await progress(sent, prepared.payloadSize, true)
            // A slow disk / large file may take several minutes to verify after network upload ends.
            return try await Self.ack(kind: "complete", transferId: transferId, socket: socket, key: key, timeout: 30 * 60)
        } onCancel: {
            socket.cancel(with: .goingAway, reason: nil)
        }
    }

    private static func sendPacket(type: UInt8, payload: Data, socket: URLSessionWebSocketTask, key: SymmetricKey) async throws {
        try Task.checkCancellation()
        try await socket.send(.data(PacketCodec.seal(type: type, payload: payload, key: key)))
    }

    private static func ack(kind: String, transferId: String, socket: URLSessionWebSocketTask,
                            key: SymmetricKey, timeout: UInt64) async throws -> TransferAck {
        let message = try await withThrowingTaskGroup(of: URLSessionWebSocketTask.Message.self) { group in
            group.addTask {
                try await withTaskCancellationHandler(operation: { try await socket.receive() },
                                                      onCancel: { socket.cancel(with: .goingAway, reason: nil) })
            }
            group.addTask {
                try await Task.sleep(nanoseconds: timeout * 1_000_000_000)
                socket.cancel(with: .goingAway, reason: nil)
                throw BridgeError.timeout
            }
            defer { group.cancelAll() }
            guard let value = try await group.next() else { throw BridgeError.invalidPacket }
            return value
        }
        guard case .data(let packet) = message else { throw BridgeError.invalidPacket }
        let plain = try PacketCodec.open(packet, key: key)
        guard plain.type == 4 else { throw BridgeError.unexpectedAck }
        let ack = try JSONDecoder().decode(TransferAck.self, from: plain.payload)
        guard ack.transferId.caseInsensitiveCompare(transferId) == .orderedSame else { throw BridgeError.unexpectedAck }
        if ack.kind == "error" { throw BridgeError.remote(ack.message ?? "Windows에서 파일을 저장할 수 없습니다.") }
        guard ack.kind == kind else { throw BridgeError.unexpectedAck }
        return ack
    }

    private static func ping(_ socket: URLSessionWebSocketTask) async throws {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                let completion = PingCompletion(continuation)
                let timeout = DispatchWorkItem {
                    completion.finish(BridgeError.timeout)
                    socket.cancel(with: .goingAway, reason: nil)
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + 20, execute: timeout)
                socket.sendPing { error in
                    timeout.cancel()
                    completion.finish(error)
                }
            }
        } onCancel: {
            socket.cancel(with: .goingAway, reason: nil)
        }
    }
}
