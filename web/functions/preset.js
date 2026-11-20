// 초대 코드 확인과 그 코드의 단추 묶음(프리셋). GET /preset?inv=<코드>
// 코드 목록·프리셋은 공개 저장소 밖 private/invites.json.
import INV from '../../private/invites.json';

export async function onRequestGet({ request }) {
  const inv = new URL(request.url).searchParams.get('inv') || '';
  const c = INV.codes && Object.prototype.hasOwnProperty.call(INV.codes, inv) ? INV.codes[inv] : null;
  if (!c) return new Response(JSON.stringify({ ok: false }), { status: 403, headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' } });
  const preset = c.preset && INV.presets ? INV.presets[c.preset] || null : null;
  return new Response(JSON.stringify({ ok: true, preset: preset }), { headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' } });
}
