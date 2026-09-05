# PocketBridge

PocketBridge는 iPhone에서 Windows로 사진, 동영상, 문서를 보내기 위한 파일 전송 프로젝트입니다. 같은 Wi-Fi가 필요 없습니다. 복잡한 서버 주소나 긴 공유 링크 대신 **6자리 PIN 방**으로 연결하는 경험을 목표로 합니다.

```text
Windows에서 방 만들기 → 6자리 PIN 표시 → iPhone에서 PIN 입력 → 파일 업로드 → Windows의 파일 목록에서 받기
```

## 현재 방향

PocketBridge는 각 전송을 독립된 방으로 처리합니다.

- 서버가 `482731` 같은 6자리 PIN과 방을 생성합니다.
- iPhone은 PocketBridge 업로드 페이지에서 PIN으로 방에 들어가 파일을 보냅니다.
- 파일은 방별 목록으로 저장됩니다.
- 방과 파일은 생성 후 24시간이 지나면 삭제됩니다.
- Windows 앱은 방의 파일 목록을 표시하고 다운로드하는 수신 프로그램이 됩니다.

이 방식에서는 사용자가 릴레이 URL을 직접 입력하지 않습니다. 서로 다른 네트워크를 연결하려면 PocketBridge 내부의 공개 서비스가 필요하며, 이 저장소는 Cloudflare Worker, D1, R2를 이용해 그 서비스를 구성합니다.

## 프로젝트 상태

PIN 방 서버와 iPhone 업로드 페이지는 `cloudflare/pocketbridge-worker`에 구현되어 있습니다. Windows 앱의 기존 QR/릴레이 전송 화면은 PIN 방 목록 수신 화면으로 전환 중입니다. 따라서 현재 커밋은 서버 구조와 기존 Windows 수신기를 함께 포함한 개발 버전입니다.

## Cloudflare 배포

무료 Cloudflare 계정이 필요합니다. 대시보드에서 **R2 Object Storage**를 먼저 활성화한 뒤 실행하세요.

```powershell
npx wrangler login
cd cloudflare/pocketbridge-worker
npx wrangler d1 execute pocketbridge-rooms --remote --file schema.sql
npx wrangler deploy
```

배포가 끝나면 Wrangler가 `https://<이름>.<계정>.workers.dev` 주소를 표시합니다. 이 주소가 PocketBridge의 고정 서비스 주소가 됩니다.

## Windows 앱 빌드

Windows와 [.NET SDK 10](https://dotnet.microsoft.com/download)이 필요합니다.

```powershell
dotnet build PocketBridge.slnx -c Release
dotnet run --project src/PocketBridge.Windows -c Release
```

배포용 Windows 실행 파일:

```powershell
pwsh ./scripts/publish-windows.ps1
```

## 저장소 구성

- `cloudflare/pocketbridge-worker`: PIN 방, 업로드 페이지, D1 방 목록, R2 파일 저장
- `src/PocketBridge.Windows`: Windows 수신 앱
- `src/PocketBridge.Core`: 파일 검증과 안전한 저장 로직
- `src/PocketBridge.Relay`: 기존 릴레이 전송 프로토콜
- `tests`: 기존 수신·릴레이 검사

## 보안과 보관

PIN은 방을 찾는 번호이며 비밀번호가 아닙니다. 실제 공개 서비스에서는 PIN 외에 방 접근 토큰, 업로드 크기 제한, 다운로드 권한, 악성 파일 대응, R2 수명 주기 규칙을 추가해야 합니다. 파일은 Cloudflare R2에 저장되므로 이 구조는 종단간 암호화가 아닙니다.

기여 방법은 [CONTRIBUTING.md](CONTRIBUTING.md), 보안 보고는 [SECURITY.md](SECURITY.md)를 참고하세요.
