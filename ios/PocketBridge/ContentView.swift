import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

private enum Palette {
    static let background = Color(red: 0.035, green: 0.05, blue: 0.085)
    static let panel = Color(red: 0.075, green: 0.10, blue: 0.15)
    static let blue = Color(red: 0.31, green: 0.54, blue: 1)
    static let muted = Color(red: 0.60, green: 0.67, blue: 0.79)
}

struct ContentView: View {
    @ObservedObject var model: SenderModel
    @State private var showScanner = false
    @State private var showFiles = false
    @State private var showCode = false
    @State private var pastedCode = ""
    @State private var selectedPhotos: [PhotosPickerItem] = []

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    hero
                    connectionCard
                    pickerCard
                    queue
                    Text("전송 중에는 앱을 열어 두세요. 모바일 데이터로도 연결되며, 통신사 데이터가 사용될 수 있어요.")
                        .font(.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                }
                .padding(24)
                .frame(maxWidth: 680)
                .frame(maxWidth: .infinity)
            }
            .background(Palette.background)
            .toolbar(.hidden, for: .navigationBar)
            .safeAreaInset(edge: .bottom) { sendBar }
        }
        .tint(Palette.blue)
        .fileImporter(isPresented: $showFiles, allowedContentTypes: [.item], allowsMultipleSelection: true, onCompletion: model.importFiles)
        .onChange(of: selectedPhotos) { _, selection in
            model.importPhotos(selection)
            selectedPhotos = []
        }
        .sheet(isPresented: $showScanner) { scannerSheet }
        .sheet(isPresented: $showCode) { codeSheet }
        .alert("확인이 필요해요", isPresented: Binding(get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } })) {
            Button("확인", role: .cancel) { model.errorMessage = nil }
        } message: { Text(model.errorMessage ?? "") }
    }

    private var hero: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 9) {
                Image(systemName: "arrow.up.right.square.fill").font(.title2).foregroundStyle(Palette.blue)
                Text("PocketBridge").font(.system(.title3, design: .rounded, weight: .bold))
                Spacer()
                Text("iPHONE → PC").font(.system(size: 10, weight: .bold, design: .monospaced)).tracking(1.1).foregroundStyle(Palette.muted)
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("가볍게 보내고,\n그대로 도착.")
                    .font(.system(size: 35, weight: .bold, design: .rounded)).tracking(-1.2)
                    .fixedSize(horizontal: false, vertical: true)
                Text("사진부터 큰 영상, 문서까지.\n서로 다른 네트워크에서도 이어집니다.")
                    .font(.subheadline).foregroundStyle(Palette.muted).lineSpacing(4)
            }
        }
    }

    private var connectionCard: some View {
        VStack(alignment: .leading, spacing: 17) {
            HStack {
                Label("01  Windows 연결", systemImage: "desktopcomputer").font(.headline)
                Spacer()
                Circle().fill(model.connected ? Color.green : Palette.muted).frame(width: 7, height: 7)
                Text(model.connected ? "연결됨" : "대기 중").font(.caption).foregroundStyle(Palette.muted)
            }
            if let invite = model.invite {
                VStack(alignment: .leading, spacing: 8) {
                    Text("연결할 중계 서버").font(.caption).foregroundStyle(Palette.muted)
                    Text(invite.displayHost).font(.system(.body, design: .monospaced, weight: .semibold)).textSelection(.enabled)
                    if !model.connected {
                        Text("Windows에 표시된 서버 주소와 같은지 확인하세요.")
                            .font(.caption).foregroundStyle(Palette.muted)
                    }
                }
                if model.connected {
                    Button("연결 해제", role: .destructive, action: model.disconnect).font(.subheadline)
                        .disabled(model.sending)
                } else {
                    Button(action: model.connect) {
                        HStack { if model.connecting { ProgressView().tint(.white) }; Text(model.connecting ? "연결 중…" : "이 Windows에 연결") }.frame(maxWidth: .infinity)
                    }.buttonStyle(PrimaryButton()).disabled(model.connecting)
                }
            }
            if !model.connected && !model.connecting {
                HStack(spacing: 12) {
                    Button { showScanner = true } label: { Label("QR 스캔", systemImage: "qrcode.viewfinder").frame(maxWidth: .infinity) }
                    Button { pastedCode = ""; showCode = true } label: { Label("코드 입력", systemImage: "doc.on.clipboard").frame(maxWidth: .infinity) }
                }.buttonStyle(SecondaryButton())
            }
            Text(model.status).font(.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
        }.card()
    }

    private var pickerCard: some View {
        VStack(alignment: .leading, spacing: 18) {
            Label("02  파일 선택", systemImage: "square.stack.3d.up").font(.headline)
            HStack(spacing: 12) {
                PhotosPicker(selection: $selectedPhotos, maxSelectionCount: 100, matching: .any(of: [.images, .videos]), preferredItemEncoding: .current) {
                    VStack(spacing: 10) {
                        Image(systemName: "photo.on.rectangle.angled").font(.title2)
                        Text("사진 · 동영상").font(.subheadline.weight(.semibold))
                    }.frame(maxWidth: .infinity).padding(.vertical, 11)
                }
                Button { showFiles = true } label: {
                    VStack(spacing: 10) {
                        Image(systemName: "folder").font(.title2)
                        Text("모든 파일").font(.subheadline.weight(.semibold))
                    }.frame(maxWidth: .infinity).padding(.vertical, 11)
                }
            }.buttonStyle(SecondaryButton()).disabled(model.busy)
            Divider().overlay(Color.white.opacity(0.06))
            Toggle(isOn: $model.smartCompression) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("스마트 문서 압축").font(.subheadline.weight(.semibold))
                    Text("텍스트 문서는 가볍게. 이미 압축된 파일은 원본으로.")
                        .font(.caption).foregroundStyle(Palette.muted)
                }
            }.disabled(model.sending)
            if model.importing {
                HStack(spacing: 10) { ProgressView(); Text("선택한 파일을 준비하고 있어요…").font(.footnote).foregroundStyle(Palette.muted) }
            }
        }.card()
    }

    private var queue: some View {
        VStack(alignment: .leading, spacing: 15) {
            HStack {
                Text("보낼 파일").font(.headline)
                Text("\(model.items.count)").font(.caption.weight(.bold)).foregroundStyle(Palette.blue)
                Spacer()
                if model.items.contains(where: { $0.state == .complete }) {
                    Button("완료 정리", action: model.clearCompleted).font(.caption).disabled(model.sending)
                }
            }
            if model.items.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "tray.and.arrow.up").font(.system(size: 30, weight: .light)).foregroundStyle(Palette.blue)
                    Text("파일을 선택하면 여기에 모여요").font(.subheadline).foregroundStyle(Palette.muted)
                    Text("‘보내기’를 눌러야 전송이 시작됩니다").font(.caption).foregroundStyle(Palette.muted.opacity(0.8))
                }.frame(maxWidth: .infinity).padding(.vertical, 30).card()
            } else {
                ForEach(model.items) { item in fileRow(item) }
            }
        }
    }

    private func fileRow(_ item: QueueItem) -> some View {
        HStack(alignment: .top, spacing: 13) {
            Image(systemName: item.state == .complete ? "checkmark.circle.fill" : "doc.fill")
                .font(.title2).foregroundStyle(item.state == .complete ? Color.green : Palette.blue)
                .frame(width: 32).padding(.top, 2)
            VStack(alignment: .leading, spacing: 7) {
                Text(item.file.name).font(.subheadline.weight(.semibold)).lineLimit(2).truncationMode(.middle)
                Text("\(byteText(item.file.size)) · \(item.detail)").font(.caption).foregroundStyle(Palette.muted)
                    .lineLimit(2)
                if item.state == .sending || item.state == .verifying {
                    ProgressView(value: item.fraction).tint(Palette.blue)
                } else if item.state == .preparing { ProgressView().controlSize(.small) }
            }
            Spacer(minLength: 0)
            if !model.sending && !model.importing {
                Button { model.remove(item.id) } label: { Image(systemName: "xmark").font(.caption).foregroundStyle(Palette.muted).padding(7) }
                    .accessibilityLabel("\(item.file.name) 목록에서 제거")
            }
        }.card()
    }

    private var sendBar: some View {
        VStack(spacing: 10) {
            if model.sending {
                Button(role: .destructive, action: model.cancelTransfer) {
                    Label("전송 중단", systemImage: "stop.fill").frame(maxWidth: .infinity)
                }.buttonStyle(SecondaryButton())
            } else {
                Button(action: model.send) {
                    HStack {
                        Text(model.waitingCount > 0 ? "\(model.waitingCount)개 파일 보내기" : "파일을 선택해 주세요")
                        Spacer()
                        Image(systemName: "arrow.up.right")
                    }
                }.buttonStyle(PrimaryButton()).disabled(!model.connected || model.busy || model.waitingCount == 0)
            }
            HStack(spacing: 5) {
                Image(systemName: "lock.fill")
                Text("기기 간 암호화 · 원본 확인 후 저장")
            }.font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.muted)
        }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 8)
            .frame(maxWidth: 680).frame(maxWidth: .infinity)
            .background(Palette.background.opacity(0.97))
    }

    private var scannerSheet: some View {
        NavigationStack {
            ZStack {
                QRScanner(onCode: { code in showScanner = false; model.acceptInvite(code) },
                          onError: { error in showScanner = false; model.errorMessage = error })
                VStack(spacing: 26) {
                    Spacer()
                    RoundedRectangle(cornerRadius: 24).stroke(Palette.blue, lineWidth: 3).frame(width: 250, height: 250)
                    Text("Windows 앱의 QR 코드를 비춰 주세요")
                        .font(.subheadline.weight(.semibold)).padding(12).background(.black.opacity(0.65), in: Capsule())
                    Spacer()
                }.allowsHitTesting(false)
            }.ignoresSafeArea(edges: .bottom).navigationTitle("Windows 연결").navigationBarTitleDisplayMode(.inline)
                .toolbar { ToolbarItem(placement: .cancellationAction) { Button("닫기") { showScanner = false } } }
        }
    }

    private var codeSheet: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 18) {
                Text("Windows 앱에서 복사한 연결 코드를 붙여넣어 주세요.")
                    .font(.subheadline).foregroundStyle(Palette.muted)
                TextEditor(text: $pastedCode).font(.system(.footnote, design: .monospaced))
                    .scrollContentBackground(.hidden).padding(12).background(Palette.panel, in: RoundedRectangle(cornerRadius: 16))
                    .autocorrectionDisabled().textInputAutocapitalization(.never).frame(minHeight: 160, maxHeight: 260)
                    .accessibilityLabel("연결 코드")
                PasteButton(payloadType: String.self) { values in if let first = values.first { pastedCode = first } }
                Text("코드에는 일회용 연결 권한이 들어 있어요. 다른 사람에게 공유하지 마세요.")
                    .font(.caption).foregroundStyle(Palette.muted)
                Button { model.acceptInvite(pastedCode); pastedCode = ""; showCode = false } label: {
                    Text("서버 확인하기").frame(maxWidth: .infinity)
                }.buttonStyle(PrimaryButton()).disabled(pastedCode.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                Spacer()
            }.padding(24).background(Palette.background)
                .navigationTitle("연결 코드 입력").navigationBarTitleDisplayMode(.inline)
                .toolbar { ToolbarItem(placement: .cancellationAction) { Button("닫기") { pastedCode = ""; showCode = false } } }
        }
    }
}

private struct PrimaryButton: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(.subheadline.weight(.bold)).foregroundStyle(.white).padding(17)
            .background(LinearGradient(colors: [Palette.blue, Color(red: 0.23, green: 0.38, blue: 0.9)], startPoint: .topLeading, endPoint: .bottomTrailing), in: RoundedRectangle(cornerRadius: 14))
            .opacity(enabled ? (configuration.isPressed ? 0.75 : 1) : 0.35)
    }
}
private struct SecondaryButton: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(.subheadline.weight(.semibold)).foregroundStyle(Palette.blue).padding(13)
            .background(Palette.blue.opacity(configuration.isPressed ? 0.2 : 0.09), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Palette.blue.opacity(0.15)))
    }
}
private extension View {
    func card() -> some View {
        padding(18).background(Palette.panel, in: RoundedRectangle(cornerRadius: 20))
            .overlay(RoundedRectangle(cornerRadius: 20).stroke(Color.white.opacity(0.055)))
    }
}
