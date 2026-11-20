import INV from '../../../private/invites.json';
// /ws/(pc|phone)/<방> → stream-relay 의 방으로 그대로 넘긴다(PC는 초대 코드 확인).
const ROOM_RE = /^[A-Za-z0-9_-]{20,64}$/;

export async function onRequest({ request, env, params }) {
  const p = params.path || [];
  if (p.length !== 2 || (p[0] !== 'pc' && p[0] !== 'phone') || !ROOM_RE.test(p[1])) {
    return new Response('not found', { status: 404 });
  }
  if (request.headers.get('Upgrade') !== 'websocket') return new Response('websocket only', { status: 426 });
  const url = new URL(request.url);
  const headers = new Headers(request.headers);
  headers.delete('X-Inv');                     // 바깥에서 붙인 것은 버린다
  if (p[0] === 'pc') {
    const inv = url.searchParams.get('inv') || '';
    if (!INV.codes || !Object.prototype.hasOwnProperty.call(INV.codes, inv)) return new Response('invite', { status: 403 });
    headers.set('X-Inv', inv);
  }
  const stub = env.ROOM.get(env.ROOM.idFromName(p[1]));
  return stub.fetch(new Request('https://room/' + p[0], { method: 'GET', headers: headers }));
}
