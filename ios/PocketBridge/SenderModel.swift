import Combine
import Foundation
import PhotosUI
import SwiftUI
import UIKit

@MainActor
final class SenderModel: ObservableObject {
    @Published var invite: PairingInvite?
    @Published var items: [QueueItem] = []
    @Published var connected = false
    @Published var connecting = false
    @Published var sending = false
    @Published var importing = false
    @Published var smartCompression = true
    @Published var status = "Windows와 연결할 준비가 됐어요"
    @Published var errorMessage: String?
    @Published var sentBytes: Int64 = 0

    private let client = TransferClient()
    private var connectionTask: Task<Void, Never>?
    private var transferTask: Task<Void, Never>?
    private var importTask: Task<Void, Never>?
    private var heartbeatTask: Task<Void, Never>?

    init() { FileStager.cleanPreviousLaunch() }
    var waitingCount: Int { items.filter { $0.state == .waiting || $0.state == .failed }.count }
    var totalBytes: Int64 { items.reduce(0) { $0 + $1.file.size } }
    var busy: Bool { sending || connecting || importing }

    func acceptInvite(_ text: String) {
        guard !sending, !connecting, !connected else { return }
        do {
            invite = try PairingInvite.parse(text.trimmingCharacters(in: .whitespacesAndNewlines))
            status = "아래 서버를 확인하고 연결을 눌러 주세요"
            errorMessage = nil
        } catch { errorMessage = error.localizedDescription }
    }

    func connect() {
        guard let invite, !connected, !connecting else { return }
        connecting = true
        status = "Windows에 연결 중"
        connectionTask = Task {
            defer { connecting = false; connectionTask = nil }
            do {
                try await client.connect(invite)
                try Task.checkCancellation()
                connected = true
                status = "연결됐어요. 보낼 파일을 선택해 주세요"
                startHeartbeat()
            } catch {
                connected = false
                if !Task.isCancelled { errorMessage = error.localizedDescription }
                status = "Windows에서 새 연결 코드를 만들어 주세요"
            }
        }
    }

    func disconnect() {
        connectionTask?.cancel()
        transferTask?.cancel()
        heartbeatTask?.cancel()
        heartbeatTask = nil
        connected = false
        invite = nil
        status = "연결이 해제됐어요. Windows에서 새 연결을 시작해 주세요"
        Task { await client.disconnect() }
    }

    func cancelTransfer() {
        transferTask?.cancel()
        disconnect()
        status = "전송을 중단했어요. 다시 연결하면 대기 파일을 재시도할 수 있어요"
    }

    func importFiles(_ result: Result<[URL], Error>) {
        guard !importing, !sending else { return }
        do {
            let urls = try result.get()
            importing = true
            importTask = Task {
                defer { importing = false; importTask = nil }
                for url in urls {
                    if Task.isCancelled { break }
                    do {
                        let worker = Task.detached(priority: .userInitiated) { try FileStager.stage(url, securityScoped: true) }
                        let file = try await worker.value
                        if Task.isCancelled { FileStager.remove(file); break }
                        items.append(QueueItem(file: file))
                    } catch { errorMessage = error.localizedDescription }
                }
            }
        } catch { errorMessage = error.localizedDescription }
    }

    func importPhotos(_ selection: [PhotosPickerItem]) {
        guard !selection.isEmpty, !importing, !sending else { return }
        importing = true
        importTask = Task {
            defer { importing = false; importTask = nil }
            for item in selection {
                if Task.isCancelled { break }
                do {
                    guard let media = try await item.loadTransferable(type: PickedMedia.self) else {
                        throw BridgeError.fileUnavailable
                    }
                    if Task.isCancelled { FileStager.remove(media.file); break }
                    items.append(QueueItem(file: media.file))
                } catch { errorMessage = error.localizedDescription }
            }
        }
    }

    func remove(_ id: UUID) {
        guard !sending, !importing, let item = items.first(where: { $0.id == id }) else { return }
        FileStager.remove(item.file)
        items.removeAll { $0.id == id }
    }

    func clearCompleted() { items.removeAll { $0.state == .complete } }

    func send() {
        guard connected, !busy, waitingCount > 0 else { return }
        let pending = items.filter { $0.state == .waiting || $0.state == .failed }.map(\.file)
        let compression = smartCompression
        sending = true
        sentBytes = 0
        UIApplication.shared.isIdleTimerDisabled = true
        transferTask = Task {
            defer {
                sending = false
                transferTask = nil
                UIApplication.shared.isIdleTimerDisabled = false
            }
            for file in pending {
                do {
                    try Task.checkCancellation()
                    update(file.id) { $0.state = .preparing; $0.fraction = 0; $0.detail = "준비 중" }
                    status = "\(file.name) 준비 중"
                    let prepared = try await FilePreparation.prepare(file, compression: compression) { [weak self] phase in
                        await self?.setPhase(file.id, phase: phase)
                    }
                    defer { if prepared.compressed { try? FileManager.default.removeItem(at: prepared.payloadURL) } }
                    let savings = prepared.compressed ? " · \(Int((1 - Double(prepared.payloadSize) / Double(file.size)) * 100))% 압축" : ""
                    status = "파일을 보내고 있어요\(savings)"
                    update(file.id) { $0.state = .sending; $0.detail = "전송 중\(savings)" }
                    let receipt = try await client.send(prepared) { [weak self] bytes, total, verifying in
                        await self?.setProgress(file.id, bytes: bytes, total: total, verifying: verifying, savings: savings)
                    }
                    try Task.checkCancellation()
                    update(file.id) { $0.state = .complete; $0.fraction = 1; $0.detail = "저장 완료 · \(receipt.fileName ?? file.name)" }
                    sentBytes += file.size
                    FileStager.remove(file)
                } catch {
                    update(file.id) { $0.state = .failed; $0.detail = Task.isCancelled ? "중단됨 · 다시 연결 후 재시도" : "전송 실패 · 다시 연결 후 재시도" }
                    if !Task.isCancelled { errorMessage = error.localizedDescription }
                    connected = false
                    invite = nil
                    heartbeatTask?.cancel()
                    await client.disconnect()
                    status = "연결이 끝났어요. Windows에서 새 연결을 시작해 주세요"
                    return
                }
            }
            status = "모두 도착했어요. Windows에서 확인해 보세요"
        }
    }

    func enteredBackground() {
        // iOS does not promise long-lived background WebSockets. Stop explicitly and retain retryable staged files.
        if sending || connected || connecting { cancelTransfer() }
    }

    private func setPhase(_ id: UUID, phase: String) { update(id) { $0.detail = phase } }
    private func setProgress(_ id: UUID, bytes: Int64, total: Int64, verifying: Bool, savings: String) {
        update(id) {
            $0.fraction = total > 0 ? min(1, Double(bytes) / Double(total)) : 1
            $0.state = verifying ? .verifying : .sending
            $0.detail = verifying ? "Windows에서 원본 확인 중" : "\(byteText(bytes)) / \(byteText(total))\(savings)"
        }
    }
    private func update(_ id: UUID, change: (inout QueueItem) -> Void) {
        if let index = items.firstIndex(where: { $0.id == id }) { change(&items[index]) }
    }

    private func startHeartbeat() {
        heartbeatTask?.cancel()
        heartbeatTask = Task {
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: 20_000_000_000)
                    try await client.healthCheck()
                } catch {
                    if !Task.isCancelled {
                        connected = false
                        status = "연결이 끊겼어요. Windows에서 새 QR 코드를 만들어 주세요"
                        transferTask?.cancel()
                        invite = nil
                        await client.disconnect()
                    }
                    return
                }
            }
        }
    }
}
