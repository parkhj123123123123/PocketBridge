import CoreTransferable
import CryptoKit
import Foundation
import UniformTypeIdentifiers
import ZIPFoundation

enum FileStager {
    // Copies live only in our own temporary directory, and are excluded from backup.
    static let root = FileManager.default.temporaryDirectory.appendingPathComponent("PocketBridge", isDirectory: true)

    static func cleanPreviousLaunch() {
        try? FileManager.default.removeItem(at: root)
    }

    static func stage(_ source: URL, securityScoped: Bool) throws -> StagedFile {
        let access = securityScoped && source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        let id = UUID()
        let folder = root.appendingPathComponent(id.uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var destination = folder.appendingPathComponent("source")
        do {
            var coordinationError: NSError?
            var copyError: Error?
            var name = source.lastPathComponent
            // File providers may need coordination to materialize iCloud / third-party files.
            NSFileCoordinator().coordinate(readingItemAt: source, options: [], error: &coordinationError) { url in
                do {
                    let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .nameKey, .fileSizeKey])
                    guard values.isRegularFile == true, values.isSymbolicLink != true else { throw BridgeError.fileUnavailable }
                    if let size = values.fileSize, Int64(size) > PacketCodec.maxFileSize { throw BridgeError.fileTooLarge }
                    name = values.name ?? source.lastPathComponent
                    try FileManager.default.copyItem(at: url, to: destination)
                } catch { copyError = error }
            }
            if let error = coordinationError { throw error }
            if let error = copyError { throw error }
            var resourceValues = URLResourceValues()
            resourceValues.isExcludedFromBackup = true
            try destination.setResourceValues(resourceValues)
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication], ofItemAtPath: destination.path)
            let attributes = try FileManager.default.attributesOfItem(atPath: destination.path)
            guard let size = attributes[.size] as? NSNumber else { throw BridgeError.fileUnavailable }
            guard size.int64Value <= PacketCodec.maxFileSize else { throw BridgeError.fileTooLarge }
            return StagedFile(id: id, url: destination, name: name, size: size.int64Value)
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    static func remove(_ file: StagedFile) {
        try? FileManager.default.removeItem(at: file.url.deletingLastPathComponent())
    }
}

struct PickedMedia: Transferable, Sendable {
    let file: StagedFile

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .movie) { received in
            PickedMedia(file: try FileStager.stage(received.file, securityScoped: false))
        }
        FileRepresentation(importedContentType: .image) { received in
            PickedMedia(file: try FileStager.stage(received.file, securityScoped: false))
        }
    }
}

enum FilePreparation {
    static let compressible: Set<String> = ["txt", "csv", "json", "xml", "log", "md", "html", "htm", "css", "js", "ts", "swift", "py", "cs", "svg", "sql", "rtf", "yaml", "yml"]

    static func prepare(_ file: StagedFile, compression: Bool, phase: @escaping @Sendable (String) async -> Void) async throws -> PreparedFile {
        let worker = Task.detached(priority: .userInitiated) {
            await phase("원본 무결성 확인 중")
            let handle = try FileHandle(forReadingFrom: file.url)
            defer { try? handle.close() }
            var digest = SHA256()
            var bytes: Int64 = 0
            while true {
                try Task.checkCancellation()
                let chunk = try handle.read(upToCount: PacketCodec.chunkSize) ?? Data()
                if chunk.isEmpty { break }
                digest.update(data: chunk)
                bytes += Int64(chunk.count)
            }
            guard bytes == file.size else { throw BridgeError.fileUnavailable }
            let hash = digest.finalize().map { String(format: "%02x", $0) }.joined()
            let raw = PreparedFile(source: file, payloadURL: file.url, payloadSize: file.size, sha256: hash)
            guard compression, file.size >= 4096,
                  compressible.contains((file.name as NSString).pathExtension.lowercased()) else { return raw }
            await phase("문서 압축 중")
            let archiveURL = file.url.deletingLastPathComponent().appendingPathComponent("payload.zip")
            try? FileManager.default.removeItem(at: archiveURL)
            do {
                let archive = try Archive(url: archiveURL, accessMode: .create)
                // A neutral single-entry name avoids leaking file names outside the encrypted manifest.
                try archive.addEntry(with: "content", type: .file, uncompressedSize: file.size,
                                     compressionMethod: .deflate, bufferSize: PacketCodec.chunkSize) { position, size in
                    try Task.checkCancellation()
                    try handle.seek(toOffset: UInt64(position))
                    return try handle.read(upToCount: size) ?? Data()
                }
                try Task.checkCancellation()
                let values = try FileManager.default.attributesOfItem(atPath: archiveURL.path)
                guard let archiveSize = values[.size] as? NSNumber else { throw BridgeError.fileUnavailable }
                let size = archiveSize.int64Value
                if Double(size) <= Double(file.size) * 0.95 {
                    return PreparedFile(source: file, payloadURL: archiveURL, payloadSize: size, sha256: hash)
                }
                try? FileManager.default.removeItem(at: archiveURL)
                return raw
            } catch {
                try? FileManager.default.removeItem(at: archiveURL)
                throw error
            }
        }
        return try await withTaskCancellationHandler(operation: { try await worker.value }, onCancel: { worker.cancel() })
    }
}
