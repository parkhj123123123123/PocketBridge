export interface Env { ROOMS: D1Database; FILES: R2Bucket; }
const json = (data: unknown, status = 200) => Response.json(data, { status, headers: { "Cache-Control": "no-store" } });
const pin = () => String(crypto.getRandomValues(new Uint32Array(1))[0] % 1_000_000).padStart(6, "0");
const token = () => crypto.randomUUID().replaceAll("-", "") + crypto.randomUUID().replaceAll("-", "");
const escape = (s: string) => s.replace(/[&<>"']/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" })[c]!);

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/health") return json({ status: "ok" });
    if (request.method === "POST" && url.pathname === "/api/rooms") {
      for (let i = 0; i < 8; i++) {
        const code = pin(), access = token(), expires = Date.now() + 86_400_000;
        const result = await env.ROOMS.prepare("INSERT OR IGNORE INTO rooms(pin, token, expires_at) VALUES(?,?,?)").bind(code, access, expires).run();
        if (result.meta.changes) return json({ pin: code, token: access, expiresAt: expires }, 201);
      }
      return json({ error: "방 번호를 만들지 못했습니다." }, 503);
    }
    const match = url.pathname.match(/^\/r\/(\d{6})(?:\/upload)?$/);
    if (!match) return new Response("Not found", { status: 404 });
    const room = await env.ROOMS.prepare("SELECT pin, expires_at FROM rooms WHERE pin=?").bind(match[1]).first<{pin:string;expires_at:number}>();
    if (!room || room.expires_at < Date.now()) return new Response("방이 만료되었습니다.", { status: 404 });
    if (request.method === "GET") return new Response(`<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><title>PocketBridge</title><main><h1>PocketBridge</h1><p>방 번호 ${escape(room.pin)}</p><form method="post" action="/r/${room.pin}/upload" enctype="multipart/form-data"><input type="file" name="file" required><button>파일 보내기</button></form></main>`, {headers:{"content-type":"text/html;charset=utf-8"}});
    if (!url.pathname.endsWith("/upload")) return new Response("Method not allowed", { status: 405 });
    const form = await request.formData(), file = form.get("file");
    if (!(file instanceof File)) return new Response("파일을 선택하세요.", { status: 400 });
    const key = `${room.pin}/${crypto.randomUUID()}`;
    await env.FILES.put(key, file.stream(), { httpMetadata: { contentType: file.type }, customMetadata: { name: file.name } });
    await env.ROOMS.prepare("INSERT INTO files(room_pin, object_key, name, bytes, created_at) VALUES(?,?,?,?,?)").bind(room.pin, key, file.name, file.size, Date.now()).run();
    return new Response("<meta name=viewport content='width=device-width'><h2>전송 완료</h2><p>Windows 앱의 방 목록에서 확인하세요.</p>", {headers:{"content-type":"text/html;charset=utf-8"}});
  }
} satisfies ExportedHandler<Env>;
