import SwiftUI

@main
struct PocketBridgeApp: App {
    @StateObject private var model = SenderModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ContentView(model: model)
                .preferredColorScheme(.dark)
                .onChange(of: scenePhase) { _, phase in
                    if phase == .background { model.enteredBackground() }
                }
        }
    }
}
