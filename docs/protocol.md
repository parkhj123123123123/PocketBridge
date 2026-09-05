# 전송 프로토콜

현재 공개 흐름은 iPhone 단축어 업로드입니다. QR에는 다음 JSON이 들어 있습니다.

```json
{"version":1,"server":"https://relay.example.com","room":"…","token":"…"}
```

단축어는 `POST /api/shortcut/{room}/upload`에 `Authorization: Bearer {token}`과 파일 본문을 보냅니다. 다음 헤더는 필수입니다.

| 헤더 | 의미 |
| --- | --- |
| `X-PocketBridge-Name64` | 원본 UTF-8 파일명의 Base64 |
| `X-PocketBridge-Original-Size` | 원본 바이트 수 |
| `X-PocketBridge-Payload-Size` | 요청 파일 본문의 바이트 수 |
| `X-PocketBridge-Compression` | `none` 또는 단일 파일 `zip` |
| `X-PocketBridge-SHA256` | 원본 파일 SHA-256, 소문자 16진수 |

릴레이는 연결된 Windows 수신자에게 `/ws/shortcut/{room}/receiver` WSS로 전달합니다. 각 이진 프레임은 `type(1 byte) + payload`이며, `manifest`, `chunk`, `end`, `ack` 순서를 사용합니다. Windows는 수신한 바이트를 임시 파일에 스트리밍하고 크기·해시·ZIP 구조를 검증한 뒤에만 원자적으로 저장합니다.

이 프로토콜은 TLS 전송 보호를 사용하며, 종단간 암호화 프로토콜이 아닙니다. 보안과 운영 경계는 [relay.md](relay.md)를 참고하세요.
