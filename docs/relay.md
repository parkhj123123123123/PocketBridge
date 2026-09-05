# 인터넷 중계 서버 실행

PocketBridge는 Windows 수신 앱과 iPhone 단축어가 접근할 수 있는 HTTPS 중계 서버를 사용합니다. 같은 Wi-Fi는 필요 없지만, 실제 iPhone 전송에는 공개 HTTPS 주소가 필요합니다. 이 저장소는 공개 서버를 운영하지 않습니다.

## 보안 경계

단축어 전송은 iOS 기본 `URL 콘텐츠 가져오기` 동작의 HTTPS POST를 사용합니다. iPhone에서 릴레이까지, 릴레이에서 Windows까지 TLS/WSS로 보호되지만 **종단간 암호화는 아닙니다**. 릴레이는 전송 중인 파일 바이트와 파일명·크기·해시 메타데이터를 볼 수 있습니다. 신뢰하는 서버만 사용하고, TLS 종료 프록시와 애플리케이션 로그에서 `Authorization` 및 `X-PocketBridge-*` 헤더를 기록하지 마세요.

Windows는 파일을 디스크에 저장하기 전 원본 SHA-256, 원본 크기, ZIP 구조를 검증합니다. 릴레이는 파일을 영구 저장하지 않고 HTTP 요청을 Windows WebSocket으로 스트리밍합니다.

## 로컬 개발

```powershell
dotnet run --project src/PocketBridge.Relay --urls http://127.0.0.1:5057
dotnet run --project tests/RelaySmoke -c Release
```

`http://127.0.0.1:5057`은 같은 Windows PC에서만 쓸 수 있습니다. iPhone의 `127.0.0.1`은 iPhone 자신이므로 실제 iPhone에는 공개 HTTPS 주소가 필요합니다.

## Docker와 HTTPS

WebSocket을 지원하는 Linux 서버와 도메인이 필요합니다.

```sh
docker build -f src/PocketBridge.Relay/Dockerfile -t pocketbridge-relay .
docker run -d --name pocketbridge-relay --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -e Relay__MaxRooms=200 \
  pocketbridge-relay
```

Nginx 등에서 신뢰할 수 있는 TLS 인증서를 설정하고 WebSocket 업그레이드를 전달합니다.

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
server {
    listen 443 ssl;
    server_name relay.example.com;
    ssl_certificate /etc/letsencrypt/live/relay.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/relay.example.com/privkey.pem;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_read_timeout 90s;
        proxy_send_timeout 90s;
        proxy_buffering off;
    }
}
```

Windows 앱에는 `https://relay.example.com`을 넣습니다. 포트 8080을 인터넷에 직접 노출하지 마세요.

## 동작과 제한

- QR 하나는 수신자 토큰과 iPhone용 송신 토큰을 포함하며 10분 후 만료됩니다. 토큰은 URL이 아닌 `Authorization: Bearer` 헤더로 보냅니다.
- 수신자 WebSocket은 `/ws/shortcut/{room}/receiver`, iPhone 업로드는 `POST /api/shortcut/{room}/upload`입니다. 자세한 형식은 [단축어 안내](shortcut.md)에 있습니다.
- 업로드는 하나씩 직렬 처리합니다. 연결이 끊기거나 검증이 실패하면 해당 파일은 저장하지 않으며 새 QR로 다시 시작합니다.
- 기본 최대 방 수는 200개이고, 상태는 단일 프로세스 메모리에 있습니다. 여러 인스턴스로 확장하려면 방 상태를 공유하도록 별도 설계가 필요합니다.
- `Relay__WaitingMinutes`(1–60), `Relay__ActiveHours`(1–24), `Relay__MaxRooms`(1–100000)로 운영 한도를 조정합니다.

구현 근거: [ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0), [Nginx에서 ASP.NET Core 호스팅](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0).
