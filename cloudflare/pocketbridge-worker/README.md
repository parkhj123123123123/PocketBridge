# PocketBridge PIN rooms

이 Worker는 6자리 PIN 방과 R2 파일 목록을 제공합니다. 방은 24시간 뒤 만료됩니다.

Cloudflare 대시보드에서 R2를 활성화한 뒤 다음을 실행합니다.

```powershell
npx wrangler d1 execute pocketbridge-rooms --remote --file schema.sql
npx wrangler deploy
```

Worker가 배포되면 Windows 앱은 `POST /api/rooms`로 방을 만들고 PIN을 표시합니다. iPhone은 `https://<worker>/r/<PIN>`에서 파일을 올립니다.
