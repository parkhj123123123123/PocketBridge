import AVFoundation
import SwiftUI
import UIKit

struct QRScanner: UIViewControllerRepresentable {
    let onCode: (String) -> Void
    let onError: (String) -> Void

    func makeUIViewController(context: Context) -> ScannerController {
        ScannerController(onCode: onCode, onError: onError)
    }

    func updateUIViewController(_ controller: ScannerController, context: Context) {}

    static func dismantleUIViewController(_ controller: ScannerController, coordinator: ()) {
        controller.stop()
    }
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let session = AVCaptureSession()
    private let cameraQueue = DispatchQueue(label: "org.pocketbridge.camera")
    private var preview: AVCaptureVideoPreviewLayer?
    private var captured = false
    private let onCode: (String) -> Void
    private let onError: (String) -> Void
    private let lifecycleLock = NSLock()
    private var stopped = false

    init(onCode: @escaping (String) -> Void, onError: @escaping (String) -> Void) {
        self.onCode = onCode
        self.onError = onError
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized: configure()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] allowed in
                if allowed { self?.configure() }
                else { self?.report("QR 코드를 읽으려면 설정에서 카메라 접근을 허용해 주세요. 연결 코드를 붙여넣을 수도 있어요.") }
            }
        default: report("카메라 접근이 꺼져 있어요. iPhone 설정에서 허용하거나 연결 코드를 붙여넣어 주세요.")
        }
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preview?.frame = view.bounds
    }

    private func configure() {
        cameraQueue.async { [weak self] in
            guard let self else { return }
            self.lifecycleLock.lock()
            let shouldStop = self.stopped
            self.lifecycleLock.unlock()
            guard !shouldStop else { return }
            guard let device = AVCaptureDevice.default(for: .video),
                  let input = try? AVCaptureDeviceInput(device: device), self.session.canAddInput(input) else {
                self.report("카메라를 사용할 수 없어요. 연결 코드를 붙여넣어 주세요.")
                return
            }
            self.session.beginConfiguration()
            self.session.addInput(input)
            let output = AVCaptureMetadataOutput()
            guard self.session.canAddOutput(output) else {
                self.session.commitConfiguration()
                self.report("QR 스캐너를 시작할 수 없어요.")
                return
            }
            self.session.addOutput(output)
            output.setMetadataObjectsDelegate(self, queue: .main)
            output.metadataObjectTypes = [.qr]
            self.session.commitConfiguration()
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                let layer = AVCaptureVideoPreviewLayer(session: self.session)
                layer.videoGravity = .resizeAspectFill
                layer.frame = self.view.bounds
                self.view.layer.insertSublayer(layer, at: 0)
                self.preview = layer
            }
            self.session.startRunning()
        }
    }

    func stop() {
        lifecycleLock.lock()
        stopped = true
        lifecycleLock.unlock()
        cameraQueue.async { [session] in if session.isRunning { session.stopRunning() } }
    }

    private func report(_ message: String) {
        DispatchQueue.main.async { [weak self] in self?.onError(message) }
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !captured,
              let code = metadataObjects.compactMap({ $0 as? AVMetadataMachineReadableCodeObject }).first?.stringValue else { return }
        captured = true
        stop()
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        onCode(code)
    }
}
