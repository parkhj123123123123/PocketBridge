# iPhone 앱 빌드와 사용

PocketBridge의 iPhone 송신기는 **SwiftUI 네이티브 앱**입니다. Safari나 웹 화면을 열지 않습니다. iOS 17 이상을 대상으로 하며, 사진·동영상은 시스템 사진 선택기로, 그 외 파일은 파일 앱 선택기로 가져옵니다. iPhone과 Windows가 같은 Wi-Fi에 있을 필요는 없습니다. 두 기기에서 접근할 수 있는 **HTTPS 중계 서버**가 필요하며, 사용 가능한 공용 서버가 기본 제공되는 것은 아닙니다. 서버 설정은 [중계 서버 문서](relay.md)를 확인하세요.

## Mac에서 만들기

Apple의 iOS SDK가 포함된 Xcode 16 이상과 XcodeGen이 필요합니다. 이 소스는 Windows 환경에서 작성·정적 검토했으며, 이 환경에서는 iOS 빌드·시뮬레이터·실기기 검증을 실행할 수 없습니다. 아래 빌드와 실제 전송 검증을 마친 뒤 배포하세요.

```sh
brew install xcodegen
cd ios
xcodegen generate
xcodebuild -resolvePackageDependencies -project PocketBridge.xcodeproj -scheme PocketBridge
xcodebuild -project PocketBridge.xcodeproj -scheme PocketBridge \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO build
open PocketBridge.xcodeproj
```

`project.yml`이 프로젝트의 원본입니다. 소스 구조나 설정을 변경하면 `xcodegen generate`로 다시 생성합니다. ZIPFoundation은 upstream [0.9.20 릴리스](https://github.com/weichsel/ZIPFoundation/releases/tag/0.9.20)로 고정했습니다. [공식 소스의 스트리밍 addEntry API](https://github.com/weichsel/ZIPFoundation/blob/0.9.20/Sources/ZIPFoundation/Archive%2BWriting.swift)를 사용합니다. 앱과 패키지의 라이선스 고지는 배포할 때 함께 유지하세요.

실제 iPhone에 설치하려면 Xcode에서 `PocketBridge` 타깃 → Signing & Capabilities → 본인의 Team을 선택하고, 고유한 Bundle Identifier로 변경한 뒤 연결한 기기에서 실행합니다. Apple 계정·기기 서명과 설치는 개발자가 처리해야 합니다. 이 저장소에는 서명 키나 개발자 계정을 넣지 않습니다. App Store/TestFlight 배포에는 별도의 Apple 배포 설정과 심사가 필요하며, 현재 소스에 App Store용 아이콘·스토어 정보·배포 서명이 포함된 것은 아닙니다.

## 테스트

설치된 시뮬레이터를 `xcrun simctl list devices available`로 확인한 뒤 해당 이름이나 ID를 사용합니다.

```sh
xcodebuild -project PocketBridge.xcodeproj -scheme PocketBridge \
  -destination 'platform=iOS Simulator,name=iPhone 16' \
  CODE_SIGNING_ALLOWED=NO test
```

테스트 소스는 잘못된 초대 코드와 안전하지 않은 주소 거부, AES-GCM 변조 거부, 원본 SHA-256, ZIP 단일 항목 복원, 이미 압축된 파일 우회, 빈 파일을 확인합니다. GitHub Actions의 macOS runner에서는 위의 서명 없는 **build** 명령을 그대로 사용할 수 있습니다. 시뮬레이터 테스트에서는 runner에 실제 설치된 기기 이름을 지정하세요. 카메라 QR 스캔, Photos/iCloud 가져오기, 셀룰러 연결, 대용량 파일 및 Windows 상호운용은 실기기에서 별도로 검증해야 합니다.

## 사용 순서

1. 중계 서버를 배포하고 Windows 앱에 해당 HTTPS 주소를 입력합니다.
2. Windows 앱에서 저장 폴더를 선택하고 새 연결을 시작합니다.
3. iPhone 앱에서 Windows의 QR을 스캔하거나 복사한 JSON 연결 코드를 입력합니다.
4. 화면의 중계 서버 주소가 Windows에 표시된 주소와 같은지 확인하고 **이 Windows에 연결**을 누릅니다. 스캔·붙여넣기만으로는 연결하지 않습니다.
5. 사진·동영상 또는 모든 파일을 선택합니다. 선택된 파일은 앱의 임시 공간에 준비되며 아직 업로드하지 않습니다.
6. **파일 보내기**를 누릅니다. Windows에서 크기와 원본 해시를 확인하고 저장한 뒤에만 ‘저장 완료’로 표시합니다.

사진은 [Apple FileRepresentation](https://developer.apple.com/documentation/coretransferable/filerepresentation)을 통해 파일로 가져오며, `.current` 인코딩 선택으로 불필요한 변환을 줄입니다. 사진 보관함의 내부 원본 파일을 직접 읽는 방식이 아니므로 사진 선택기가 제공하는 현재 표현을 전송합니다. Live Photo를 원본 사진+동영상 쌍으로 보존하는 전용 내보내기 기능은 없습니다. 보존해야 하는 정확한 파일이 있다면 파일 앱에서 그 파일을 선택하세요. 다른 앱의 비공개 저장공간·시스템 파일은 iOS 권한상 선택할 수 없습니다. 폴더 자체의 재귀 전송은 지원하지 않으며, 파일 앱에서 미리 ZIP으로 묶으면 전송할 수 있습니다.

## 압축과 대용량 파일

4 KiB 이상인 텍스트 계열 문서만 스트리밍 ZIP/deflate 후보로 삼습니다. 완성된 ZIP이 원본보다 **5% 이상 작을 때** 사용하며, 그 외에는 원본을 보냅니다. `txt`, `csv`, `json`, `xml`, `log`, `md`, 코드·마크업·설정 파일 등이 대상입니다. PDF, Office의 `docx/xlsx/pptx`, ZIP, 사진과 영상처럼 이미 압축된 형식은 다시 압축하지 않습니다. 압축의 디스크 I/O·CPU 비용과 실제 네트워크 속도에 따라 총 소요 시간은 달라지므로, 항상 가장 빠르다고 보장하지 않습니다. 필요하면 토글로 압축을 끌 수 있습니다.

현재 버전의 파일당 크기 제한은 **100 GiB**입니다. 원본 SHA-256과 ZIP 읽기, 네트워크 읽기는 256 KiB 단위입니다. 전체 사진이나 영상을 하나의 `Data`로 메모리에 올리지 않습니다. 대신 선택된 원본의 **앱 내 임시 복사 공간**이 필요하며, 압축 대상은 ZIP 공간도 추가로 필요합니다. 성공한 파일의 임시 복사는 삭제하고, 실패한 파일은 현재 실행 중 재시도할 수 있도록 유지합니다. 목록에서 지우거나 다음 앱 실행 시 정리합니다. 사용자 원본은 삭제하지 않습니다.

## 연결과 보안 동작

- HTTP는 `localhost`, `127.0.0.1`, `::1` 개발 환경만 허용합니다. 실기기의 localhost는 그 iPhone 자신이므로 Windows PC를 가리키지 않습니다. 실기기 테스트도 접근 가능한 HTTPS 주소를 사용하세요.
- QR에는 서버 주소·일회용 송신 토큰·32바이트 암호 키가 있습니다. QR/코드는 비밀이며 타인에게 공유하면 안 됩니다. 토큰은 Authorization 헤더, 키는 암호화에만 사용합니다. 키를 서버 URL·로그·중계 서버에 보내지 않습니다. 서버 리디렉션도 거부합니다.
- 모든 파일명·해시·제어 메시지·파일 조각은 AES-256-GCM으로 암호화합니다. 프레임은 `version(1) + nonce(12) + ciphertext + tag(16)`입니다. 암호화 nonce는 CryptoKit이 프레임마다 생성합니다.
- 파일마다 manifest → ready 확인 → 조각들 → end → complete 확인 순서를 지킵니다. 서버 자체는 암호문을 전달합니다. 별도의 공개키 인증, 장기 신원 관리, 외부 보안 감사까지 구현한 제품은 아닙니다.
- 전송 중 화면 자동 잠금을 막습니다. iOS는 장시간 백그라운드 WebSocket 실행을 보장하지 않으므로 앱이 백그라운드로 들어가면 연결을 명시적으로 중단합니다. 앱을 열어 둔 상태에서 사용하세요.
- 중단·연결 끊김 시 자동 재개하지 않습니다. Windows에서 새 QR을 만들고 다시 연결한 뒤 대기 파일을 재시도합니다. Windows에 저장된 직후 완료 응답을 받지 못한 경우 이미 저장됐을 수도 있으며, 재전송 시 Windows의 중복 이름 방지 규칙에 따라 별도 파일이 생길 수 있습니다.
- 수신 준비 확인은 60초, 최종 파일 검증 확인은 최대 30분을 기다립니다. 대기 중 연결 상태는 주기적인 WebSocket ping으로 확인합니다.

파일 가져오기는 플랫폼의 [PhotosPicker](https://developer.apple.com/documentation/photosui/photospicker)와 보안 범위가 있는 URL 접근·NSFileCoordinator를 사용합니다. 사진은 사용자 선택 범위만 접근하므로 보관함 전체 권한을 요청하지 않으며, QR을 스캔할 때만 카메라 권한을 요청합니다.
