# PocketBridge

PocketBridge는 iPhone의 사진, 동영상, 문서를 Windows PC로 보내는 네이티브 수신 앱입니다. iPhone에서는 별도 앱 대신 **단축어 공유 시트**를 사용하므로 Mac, Xcode, Apple Developer 유료 계정이 필요 없습니다. 두 기기가 같은 Wi-Fi에 있을 필요도 없습니다.

Windows 앱에서 QR을 만들고, iPhone에서 파일을 선택해 **공유 → PocketBridge → QR 스캔**을 누르면 전송됩니다.

## 제공 기능

- Windows 10/11 네이티브 WPF 수신 앱
- iPhone 단축어의 사진·동영상·파일 공유 시트 입력
- HTTPS/WSS 스트리밍: 전체 파일을 메모리에 올리지 않음
- SHA-256 원본 해시와 원본 크기 검증, 임시 파일의 원자적 저장, 기존 파일 덮어쓰기 방지
- 문서용 단일 파일 ZIP 압축 레시피와 압축 후 원본 복원
- 한 번만 쓸 수 있는 QR 토큰과 10분 연결 만료

## 먼저 알아둘 점

- 다른 네트워크에서 보내려면 두 기기가 접근할 **HTTPS 중계 서버**가 필요합니다. 이 저장소는 공개 릴레이를 제공하지 않습니다.
- 단축어 버전은 iOS 기본 동작만 사용하므로 파일 내용은 HTTPS로 전송되고 릴레이를 경유합니다. **종단간 암호화가 아닙니다.** 신뢰하는 서버를 운영하고 HTTPS를 사용하세요. Windows는 도착한 파일의 SHA-256과 크기를 검증합니다.
- iOS는 사용자가 공유 시트에서 선택한 항목만 단축어에 전달합니다. iPhone 전체 저장소나 다른 앱의 비공개 영역을 자동으로 탐색할 수 없습니다.

## 사용하기

1. [Windows 앱](#windows-앱-빌드)을 실행하고, 중계 서버 HTTPS 주소와 저장 폴더를 지정합니다.
2. **연결 QR 만들기**를 누릅니다.
3. iPhone에서 [PocketBridge 단축어](docs/shortcut.md)를 한 번 만듭니다.
4. 사진 앱 또는 파일 앱에서 보낼 항목을 선택하고 **공유 → PocketBridge**를 누릅니다.
5. 단축어가 Windows QR을 스캔하면 업로드가 끝날 때까지 iPhone을 잠금 해제 상태로 둡니다.

## Windows 앱 빌드

Windows와 [.NET SDK 10](https://dotnet.microsoft.com/download)이 필요합니다.

```powershell
dotnet build PocketBridge.slnx -c Release
dotnet run --project src/PocketBridge.Windows -c Release
```

배포용 단일 파일은 다음과 같이 만듭니다.

```powershell
pwsh ./scripts/publish-windows.ps1
```

## 중계 서버 실행

개발용 로컬 실행:

```powershell
dotnet run --project src/PocketBridge.Relay --urls http://127.0.0.1:5057
```

실제 iPhone 전송에는 공개 HTTPS 주소와 WebSocket 프록시 설정이 필요합니다. [중계 서버 안내](docs/relay.md)를 따르세요.

## 개발·검증

```powershell
dotnet build PocketBridge.slnx -c Release
dotnet run --project tests/PocketBridge.Tests -c Release
dotnet run --project tests/RelaySmoke -c Release
```

릴레이를 실행한 상태에서는 다음 통합 검사가 추가됩니다.

```powershell
dotnet run --project tests/PocketBridge.Tests -c Release -- --relay http://127.0.0.1:5057
```

## 저장소 구성

- `src/PocketBridge.Windows`: Windows 수신 앱
- `src/PocketBridge.Relay`: HTTPS/WSS 중계 서버
- `src/PocketBridge.Core`: 검증, 안전한 저장, 수신 프로토콜
- `docs/shortcut.md`: iPhone 단축어 만들기
- `docs/relay.md`: 서버 배포와 보안 경계
- `tests`: 단위·통합·릴레이 스모크 검사
- `cloudflare/pocketbridge-worker`: PIN 방·파일 목록용 Cloudflare Worker와 R2 저장소 구성

기여 방법은 [CONTRIBUTING.md](CONTRIBUTING.md), 보안 보고는 [SECURITY.md](SECURITY.md)를 참고하세요.
