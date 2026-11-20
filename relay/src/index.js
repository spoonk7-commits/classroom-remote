// stream-relay — 교실 리모컨 중계 방.
// PC 하나 = 방 하나(Durable Object). 폰이 보낸 것은 PC로, PC가 보낸 것은 폰으로 넘겨 주기만 한다.
// 내용은 읽지 않고 저장하지 않는다. 바깥 주소는 Pages Functions가 여기로 넘긴다(이 워커 주소는 바깥에 열지 않음).
// 초대 코드: PC가 붙을 때 Pages Functions가 코드를 확인해 X-Inv 로 넘기면 방에 적어 둔다.
// 폰은 초대 코드가 적힌(지금도 살아 있는) 방에만 붙을 수 있다. 코드 목록은 공개 저장소 밖 private/invites.json.
import INV from '../../private/invites.json';

const ROOM_RE = /^[A-Za-z0-9_-]{20,64}$/;
const MAX_MSG = 640 * 1024;         // 파일 조각(256KB, 암호화·base64 두 번 → 약 460KB)까지
const MAX_PHONES = 4;               // 한 PC에 붙는 기기 수

export default {
  // 바깥에서 바로 오는 요청은 받지 않는다(Pages Functions → 방 바인딩으로만)
  async fetch() { return new Response('not found', { status: 404 }); },
};
function live(inv) { return !!(inv && INV.codes && Object.prototype.hasOwnProperty.call(INV.codes, inv)); }

export class Room {
  constructor(state) {
    this.state = state;
    // 연결 유지 신호는 방을 깨우지 않고 자동으로 답한다(요금 없음).
    state.setWebSocketAutoResponse(new WebSocketRequestResponsePair('p', 'o'));
  }

  async fetch(req) {
    const role = new URL(req.url).pathname.slice(1);
    if (role === 'pc') {
      const inv = req.headers.get('X-Inv');
      if (!live(inv)) return new Response('invite', { status: 403 });
      if ((await this.state.storage.get('inv')) !== inv) await this.state.storage.put('inv', inv);
    } else {
      if (!live(await this.state.storage.get('inv'))) return new Response('invite', { status: 403 });
    }
    if (role === 'phone') this.prunePhones();
    if (role === 'pc') {
      // PC는 하나만. 새로 붙으면 옛 연결은 닫는다(프로그램을 다시 켠 경우).
      for (const old of this.state.getWebSockets('pc')) { try { old.close(4000, 'replaced'); } catch (e) {} }
    }
    const pair = new WebSocketPair();
    const id = role === 'pc' ? 'pc' : 'd' + Math.random().toString(36).slice(2, 8);
    this.state.acceptWebSocket(pair[1], [role, id]);
    pair[1].serializeAttachment({ t: Date.now() });
    if (role === 'phone') this.toPc({ t: 'join', from: id });
    else this.toPhones({ t: 'pc', on: true });
    if (role === 'phone') {
      const on = !!this.livePc();
      pair[1].send(JSON.stringify({ t: 'pc', on }));
    }
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  async webSocketMessage(ws, msg) {
    if (typeof msg !== 'string' || msg.length > MAX_MSG) return;
    const [role, id] = this.state.getTags(ws);
    if (role === 'phone') {
      // 폰 → PC: 누가 보냈는지만 붙여서 넘긴다. 내용은 그대로(문자열 이어 붙이기, 해석하지 않음).
      const pc = this.livePc();
      if (pc) pc.send('{"from":"' + id + '","m":' + JSON.stringify(msg) + '}');
      else ws.send(JSON.stringify({ t: 'pc', on: false }));
    } else {
      // PC → 폰: {"to":"d…"} 이 있으면 그 기기만, 없으면 모두. 바깥 껍데기만 본다.
      let to = null, body = msg;
      if (msg.startsWith('{"to":"')) {
        const end = msg.indexOf('","m":');
        if (end > 0) { to = msg.slice(7, end); body = JSON.parse(msg.slice(end + 6, -1)); }
      }
      for (const p of this.state.getWebSockets('phone')) {
        if (!to || this.state.getTags(p)[1] === to) { try { p.send(body); } catch (e) {} }
      }
    }
  }

  async webSocketClose(ws, code) {
    const [role, id] = this.state.getTags(ws);
    if (role === 'phone') this.toPc({ t: 'leave', from: id });
    else if (code !== 4000) this.toPhones({ t: 'pc', on: false });
    try { ws.close(1000, 'bye'); } catch (e) {}
  }

  async webSocketError(ws) { return this.webSocketClose(ws, 1011); }

  // 연결이 마지막으로 살아 있던 때: 붙은 때와 자동 응답(p→o) 때 가운데 늦은 것
  seen(w) {
    const a = (w.deserializeAttachment && w.deserializeAttachment()) || {};
    const r = this.state.getWebSocketAutoResponseTimestamp(w);
    return Math.max(a.t || 0, r ? r.getTime() : 0);
  }
  // 강제로 끈 PC의 연결이 남아 있을 수 있다: 가장 최근에 붙은 PC 연결 하나
  livePc() {
    let best = null, bt = -1;
    for (const w of this.state.getWebSockets('pc')) {
      const a = (w.deserializeAttachment && w.deserializeAttachment()) || {};
      if ((a.t || 0) > bt) { bt = a.t || 0; best = w; }
    }
    return best;
  }
  // 폰: 90초 넘게 말이 없으면 끊긴 것으로 보고 닫는다. 그래도 많으면 오래된 것부터 닫는다(새 연결은 막지 않음).
  prunePhones() {
    const now = Date.now();
    let list = this.state.getWebSockets('phone');
    for (const w of list) if (now - this.seen(w) > 90000) { try { w.close(4001, 'stale'); } catch (e) {} }
    list = this.state.getWebSockets('phone').filter((w) => now - this.seen(w) <= 90000)
      .sort((a, b) => this.seen(a) - this.seen(b));
    while (list.length >= MAX_PHONES) { const w = list.shift(); try { w.close(4002, 'too many'); } catch (e) {} }
  }

  toPc(obj) {
    const pc = this.livePc();
    if (pc) { try { pc.send(JSON.stringify(obj)); } catch (e) {} }
  }
  toPhones(obj) {
    const s = JSON.stringify(obj);
    for (const p of this.state.getWebSockets('phone')) { try { p.send(s); } catch (e) {} }
  }
}
