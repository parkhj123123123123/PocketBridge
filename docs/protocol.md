# PocketBridge protocol v1

이 문서는 Windows 수신 앱, iOS 송신 앱, 중계 서버가 함께 사용하는 공개 상호운용 규격입니다. 모든 정수 크기는 JSON number로 표현하며 파일 크기는 64비트 부호 있는 정수 범위에서 처리합니다.

## 페어링

수신자는 `POST /api/rooms`로 일회용 방을 만듭니다. 응답은 다음 camelCase JSON입니다.

```json
{
  "roomId": "32-character-lowercase-hex",
  "receiverToken": "43-character-base64url",
  "senderToken": "43-character-base64url",
  "expiresAt": "RFC-3339 timestamp"
}
```

수신자는 `/ws/{roomId}/receiver`, 송신자는 `/ws/{roomId}/sender` WebSocket에 접속하며 자신의 토큰을 `Authorization: Bearer …` 헤더로 보냅니다. 운영 환경은 WSS만 허용합니다. HTTP/WS는 loopback 개발 주소에서만 허용합니다.

수신자가 QR 또는 클립보드로 송신자에게 전달하는 UTF-8 JSON은 다음과 같습니다.

```json
{
  "version": 1,
  "server": "https://relay.example.com",
  "room": "32-character-hex",
  "token": "sender-token",
  "key": "base64-encoded-32-byte-key"
}
```

`key`는 중계 서버로 보내지 않습니다. 초대를 읽은 앱은 서버 호스트를 표시하고 사용자가 연결 버튼을 누른 뒤에 접속합니다.

## 암호화 패킷

각 WebSocket binary message는 독립적으로 인증됩니다.

```text
offset  size                 value
0       1                    protocol version: 0x01
1       12                   random AES-GCM nonce
13      plaintext length     ciphertext
...     16                   AES-GCM authentication tag
```

AES-256-GCM을 사용하며 AAD는 없습니다. 같은 키에서 nonce를 재사용하면 안 됩니다. 복호화된 내용의 첫 바이트는 메시지 종류이고 나머지는 payload입니다.

| 종류 | 값 | payload |
|---|---:|---|
| manifest | 1 | UTF-8 JSON `TransferManifest` |
| chunk | 2 | 파일 payload 바이트, 최대 256 KiB |
| end | 3 | UTF-8 JSON `{ "transferId": "UUID" }` |
| ack | 4 | UTF-8 JSON `TransferAck` |

manifest 형식:

```json
{
  "transferId": "UUID",
  "fileName": "basename",
  "originalSize": 1234,
  "payloadSize": 842,
  "compression": "none or zip",
  "sha256": "64-character-lowercase-hex-of-original"
}
```

ack 형식:

```json
{
  "kind": "ready or complete or error",
  "transferId": "UUID",
  "fileName": "optional committed name",
  "message": "optional error text"
}
```

## 상태 순서

파일은 한 번에 하나씩 순서대로 처리합니다.

1. 송신자가 manifest를 보내고 같은 `transferId`의 `ready` ack를 기다립니다.
2. 송신자가 payload를 256 KiB 이하 chunk로 보냅니다.
3. 송신자가 end를 보내고 `complete` ack를 기다립니다.
4. 수신자는 payload 크기, 원본 크기, 원본 SHA-256을 모두 확인하고 파일을 디스크에 커밋한 뒤에만 `complete`를 보냅니다.

한 페어링에서는 서로 다른 `transferId`의 manifest를 최대 10,000개까지 받아들입니다. 수신자는 이미 받아들인 UUID를 연결이 끝날 때까지 보관하고, 같은 ID가 다시 오면 재전송 공격으로 판단하여 거부하고 연결을 종료합니다. UUID의 대소문자나 표기 방식이 달라도 같은 ID로 처리합니다. 10,000개 제한에 도달하면 새 QR로 페어링해야 합니다. 이 제한으로 재전송 방지 기록의 메모리 사용량을 일정 범위 안으로 제한합니다. 새 페어링에서는 이전 파일을 다시 전송할 수 있습니다.

`compression`이 `zip`이면 payload는 디렉터리가 아닌 단일 entry를 가진 ZIP이어야 합니다. 수신자는 ZIP 내부 이름을 저장 경로에 사용하지 않고 manifest의 안전하게 정리된 basename을 사용합니다. 선언된 원본 크기를 넘는 압축 해제, 다중 entry, 잘못된 해시를 거부합니다.

연결이 끊어지면 해당 방은 소비된 것으로 처리합니다. 미완료 임시 파일을 삭제하고 새 방에서 파일 전체를 다시 보내야 합니다. v1에는 이어받기나 재접속이 없습니다.
