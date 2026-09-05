# 인터넷 중계 서버 실행

PocketBridge는 Windows와 iPhone에서 각각 네이티브 앱으로 실행합니다. 서로 다른 Wi-Fi나 이동통신망에서 연결하려면 두 앱이 접근할 수 있는 인터넷 중계 서버가 필요합니다. 이 프로젝트에는 공개 서버 주소가 포함되어 있지 않습니다. 서버를 한 번 배포한 뒤 두 앱에서 같은 HTTPS 주소를 사용합니다.

중계 서버는 웹 화면을 제공하지 않습니다. 파일은 두 기기 사이에서 AES-256-GCM으로 암호화되며, 복호화 키는 초대 정보에만 들어갑니다. 서버는 키를 받지 않고 암호화된 메시지를 메모리로 전달합니다. 파일 내용을 디스크에 저장하지 않으며 파일명도 암호화된 메시지 안에 있습니다. 서버 운영자는 IP 주소, 접속 시간, 전송량을 볼 수 있습니다.

## 로컬 개발

저장소 최상위 폴더에서 .NET 10 SDK로 실행합니다.

```powershell
dotnet run --project src/PocketBridge.Relay --urls http://127.0.0.1:5057
```

Windows 앱에 `http://127.0.0.1:5057`을 입력하면 같은 PC에서 개발할 수 있습니다. iPhone의 `127.0.0.1`은 iPhone 자체이므로 이 주소로 PC에 연결할 수 없습니다. 실제 iPhone 및 서로 다른 네트워크 테스트에는 공개 HTTPS 서버를 사용합니다. 인증서 검증을 끄거나 자체 서명 인증서를 무조건 신뢰하도록 앱을 바꾸지 않습니다.

실제 HTTP/WebSocket 연결 검사:

```powershell
dotnet run --project tests/RelaySmoke -c Release
```

검사 프로그램은 임시 포트에 서버를 시작하고 인증, 중복 연결 거부, 양방향 이진 메시지, 분할 메시지, 연결 정리, 평문 거부, 크기 제한, 방 수 제한을 확인한 뒤 종료합니다.

## Docker와 HTTPS

WebSocket을 지원하는 Linux 서버와 도메인이 필요합니다. 아래 예시는 호스트의 Nginx가 HTTPS를 처리하고 Docker 서버에는 로컬로만 연결하는 구성입니다. 호스팅 서비스의 연결 유지 시간, 대역폭 비용, 전송량 제한도 확인합니다.

```sh
docker build -f src/PocketBridge.Relay/Dockerfile -t pocketbridge-relay .
docker run -d --name pocketbridge-relay --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -e Relay__MaxRooms=200 \
  pocketbridge-relay
```

`relay.example.com`을 실제 도메인으로 바꾸고 신뢰할 수 있는 TLS 인증서를 발급합니다. Nginx 설정에서 다음과 같이 WebSocket 업그레이드를 전달합니다. 인증서 경로는 서버 환경에 맞게 지정합니다.

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}

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

이후 Windows 앱에 `https://relay.example.com`을 입력합니다. `GET /health`가 `{"status":"ok","protocol":1}`을 반환하고 실제 두 기기 연결이 되는지 확인합니다. 포트 8080을 외부에 노출하지 않습니다.

## 운영 범위와 제한

- 기본 최대 200개 방, 방 생성 10회/분/IP, 전체 요청 120회/분/IP입니다. 초과 시 각각 HTTP 503 또는 429를 반환합니다. 위 프록시 구성에서는 앱이 프록시 IP를 보므로 제한이 전체 사용자에게 합산됩니다. 소규모 개인 서버에 적합하며, 공개 서비스로 확장할 때는 프록시에서 실제 IP별 제한을 적용하고 신뢰할 프록시를 명시적으로 구성해야 합니다. 외부 `X-Forwarded-For`를 무조건 신뢰하지 않습니다.
- 연결 대기는 10분, 두 기기 연결 후에는 최대 12시간입니다. 만료 방은 15초 간격으로 정리합니다. `Relay__WaitingMinutes`(1–60), `Relay__ActiveHours`(1–24), `Relay__MaxRooms`(1–100000) 환경 변수로 조정할 수 있습니다.
- 방마다 수신자 한 명과 송신자 한 명만 연결할 수 있습니다. 역할별 256비트 토큰을 사용하며 서버에는 토큰의 SHA-256 해시만 보관합니다. 토큰은 URL이 아닌 `Authorization` 헤더로 전송합니다. 프록시 및 APM에서 요청 헤더/응답 본문을 기록하지 않도록 유지합니다.
- WebSocket 메시지 하나의 최대 크기는 1 MiB입니다. 암호화 청크는 256 KiB 이하 원문으로 전송하므로 큰 파일은 작은 메시지 여러 개로 전달됩니다. 서버는 한 방향당 64 KiB 버퍼로 전송하고 상대가 느리면 전송을 기다립니다. 전체 파일을 메모리에 읽지 않습니다.
- 연결이 끊기면 해당 방과 상대 연결을 종료합니다. 현재 버전은 끊긴 파일의 이어받기를 지원하지 않습니다. 새 초대를 만들고 파일을 다시 보내야 합니다. 서버 재시작/배포도 활성 전송을 끊습니다.
- 방 상태는 단일 프로세스 메모리에 있습니다. 기본 배포는 인스턴스 하나를 사용합니다. 단순 로드밸런싱이나 자동 수평 확장으로는 두 기기가 같은 방에 도달하지 않을 수 있습니다.
- 서버는 WebSocket 메시지 압축을 켜지 않습니다. 문서 압축은 송신 앱에서 암호화 전에 수행합니다. 중계 서버에 파일 저장소나 복호화 기능을 추가하지 않아도 기본 전송이 가능합니다.

## 구현 근거

- [ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0): 요청 수명 유지와 keepalive 설정.
- [WebSocket.SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket.sendasync?view=net-10.0): 소켓 하나의 송신 작업은 직렬로 실행.
- [Nginx에서 ASP.NET Core 호스팅](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0): HTTPS 프록시와 WebSocket 업그레이드 설정.
- [Apple URLSessionWebSocketTask](https://developer.apple.com/documentation/foundation/urlsessionwebsockettask) 및 [maximumMessageSize](https://developer.apple.com/documentation/foundation/urlsessionwebsockettask/maximummessagesize): iOS 네이티브 메시지 전송 및 메시지별 버퍼 제한.
