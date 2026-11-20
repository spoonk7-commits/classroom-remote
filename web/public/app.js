// 교실 리모컨 — 폰 화면. 주소 #r=<방>&k=<열쇠> 로 PC의 방을 찾아 붙는다.
// #뒤는 서버로 가지 않는다. 홈 화면 바로가기에도 그대로 들어가게 주소를 지우지 않는다.
(function () {
  'use strict';
  var $ = function (id) { return document.getElementById(id); };

  // ---- 방 정보 ----
  var hp = new URLSearchParams(location.hash.slice(1));
  var room = hp.get('r'), key = hp.get('k');
  if (room && key) { try { localStorage.setItem('pair', JSON.stringify({ r: room, k: key })); } catch (e) {} }
  else { try { var s = JSON.parse(localStorage.getItem('pair') || 'null'); if (s) { room = s.r; key = s.k; } } catch (e) {} }

  var views = { deck: $('deck'), pad: $('pad'), att: $('att'), files: $('files'), wait: $('wait') };
  var tab = 'deck', ready = false, cur = 'wait';
  // 넓은 화면(패드 전체)이면 단추·제스처 판을 한 화면에, 좁으면(폰·화면 나누기) 탭으로
  function isWide() { return window.innerWidth >= 900 && window.innerHeight >= 500; }
  function show(name) {
    cur = name;
    var wide = document.body.classList.contains('wide');
    for (var k in views) {
      var on = name === 'wait' || name === 'att' || name === 'files' ? k === name : (wide ? (k === 'deck' || k === 'pad') : k === name);
      views[k].classList.toggle('on', on);
    }
    $('appbar').hidden = !(wide && (name === 'deck' || name === 'pad') && appData && appData.name);
  }
  function layout() {
    document.body.classList.toggle('wide', isWide());
    show(cur);
  }
  function waitMsg(t, again) { $('waitText').innerHTML = t; $('again').hidden = !again; if (!ready) show('wait'); }

  document.querySelectorAll('.tab').forEach(function (b) {
    b.addEventListener('click', function () {
      tab = b.dataset.tab;
      document.querySelectorAll('.tab').forEach(function (x) { x.classList.toggle('on', x === b); x.setAttribute('aria-selected', x === b); });
      if (ready) show(tab);
      if (ready && tab === 'att') openAtt();
      if (ready && tab === 'files') openFiles();
    });
  });

  var toastT = 0;
  function toast(t) {
    var el = $('toast'); el.textContent = t; el.classList.add('on');
    clearTimeout(toastT); toastT = setTimeout(function () { el.classList.remove('on'); }, 1100);
  }
  function buzz() { if (navigator.vibrate) navigator.vibrate(12); }

  $('pw').hidden = !(room && key);
  if (!room || !key) { waitMsg('PC 리모컨 프로그램의 QR을<br>카메라로 찍어 주세요.<br><br><a class="lnk" href="get">PC 프로그램 받기</a> · <a class="lnk" href="guide">쓰는 법</a>'); return; }

  // ---- 암호화: QR 속 열쇠 K → 암호 열쇠(AES-256-CBC) · 확인 열쇠(HMAC-SHA256) ----
  // 보내는 모양: "E" + base64(iv 16 | 암호문 | 확인값 32). 열쇠 자체는 보내지 않는다.
  var te = new TextEncoder(), td = new TextDecoder(), subtle = window.crypto && crypto.subtle;
  function b64urlToBytes(s) { s = s.replace(/-/g, '+').replace(/_/g, '/'); while (s.length % 4) s += '='; var b = atob(s), u = new Uint8Array(b.length); for (var i = 0; i < b.length; i++) u[i] = b.charCodeAt(i); return u; }
  function toB64(u) { var s = ''; for (var i = 0; i < u.length; i += 0x8000) s += String.fromCharCode.apply(null, u.subarray(i, i + 0x8000)); return btoa(s); }
  function fromB64(s) { var b = atob(s), u = new Uint8Array(b.length); for (var i = 0; i < b.length; i++) u[i] = b.charCodeAt(i); return u; }
  var keysP = !subtle ? Promise.reject(new Error('nocrypto')) : subtle.importKey('raw', b64urlToBytes(key), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']).then(function (k) {
    return Promise.all([subtle.sign('HMAC', k, te.encode('enc')), subtle.sign('HMAC', k, te.encode('mac'))]);
  }).then(function (r) {
    return Promise.all([
      subtle.importKey('raw', r[0], { name: 'AES-CBC' }, false, ['encrypt', 'decrypt']),
      subtle.importKey('raw', r[1], { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify'])
    ]);
  });
  function seal(obj) {
    return keysP.then(function (k) {
      var iv = crypto.getRandomValues(new Uint8Array(16));
      return subtle.encrypt({ name: 'AES-CBC', iv: iv }, k[0], te.encode(JSON.stringify(obj))).then(function (ct) {
        var body = new Uint8Array(16 + ct.byteLength); body.set(iv, 0); body.set(new Uint8Array(ct), 16);
        return subtle.sign('HMAC', k[1], body).then(function (tag) {
          var all = new Uint8Array(body.length + 32); all.set(body, 0); all.set(new Uint8Array(tag), body.length);
          return 'E' + toB64(all);
        });
      });
    });
  }
  function unseal(str) {
    return keysP.then(function (k) {
      var all = fromB64(str.slice(1)); if (all.length < 64) throw new Error('short');
      var body = all.subarray(0, all.length - 32), tag = all.subarray(all.length - 32);
      return subtle.verify('HMAC', k[1], tag, body).then(function (ok) {
        if (!ok) throw new Error('mac');
        return subtle.decrypt({ name: 'AES-CBC', iv: body.subarray(0, 16) }, k[0], body.subarray(16));
      }).then(function (pt) { return JSON.parse(td.decode(pt)); });
    });
  }

  // ---- 연결 ----
  var ws = null, pcOn = false, tries = 0, pingT = 0, retryT = 0, manual = false;
  var sess = null, seq = 0, chain = Promise.resolve();
  function setDot() { $('dot').classList.toggle('on', !!(ws && ws.readyState === 1 && pcOn && ready)); }
  // 차례를 지켜 잠가 보낸다. 연결마다 PC가 준 번호(sn)와 늘어나는 순번(s)을 붙여 다시 보내기를 막는다.
  function send(o) {
    var w = ws; if (!w || w.readyState !== 1) return false;
    if (o.t !== 'hello') { if (!sess) return false; o.sn = sess; o.s = ++seq; }
    chain = chain.then(function () { return seal(o); }).then(function (s) { if (w.readyState === 1) w.send(s); }).catch(function () {});
    return true;
  }
  function onPlain(m) {
    if (m.t === 'pc') {
      pcOn = m.on; sess = null;
      if (pcOn) send({ t: 'hello' });
      else { ready = false; waitMsg('PC 리모컨 프로그램이 꺼져 있습니다.'); }
    } else if (m.t === 'bad') {
      ready = false; sess = null; waitMsg('연결 정보가 맞지 않습니다.<br>PC의 QR을 다시 찍어 주세요.');
    }
    setDot();
  }
  function onSealed(m) {
    if (m.t === 'deck') { sess = m.sn || null; seq = 0; ready = true; deckData = m; buildDeck(); show(tab); if (tab === 'att') openAtt(); if (tab === 'files') openFiles(); }
    else if (m.t === 'app') { onApp(m); }
    else if (m.t === 'tt') { onTT(m); }
    else if (m.t === 'roster') { onRoster(m); }
    else if (m.t === 'fdone') { onFileDone(m); }
    else if (m.t === 'flist') { onFileList(m); }
    else if (m.t === 'fdata') { onFileData(m); }
    else if (m.t === 'ok') { if (m.msg) toast(m.msg); if (m.saved === 'att') { att.dirty = false; attSum(); } }
    setDot();
  }

  if (!subtle) { waitMsg('이 브라우저에서는 쓸 수 없습니다.<br>크롬이나 사파리로 여세요.'); return; }

  function connect() {
    clearTimeout(retryT);
    if (manual) return;
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    ws = new WebSocket(proto + location.host + '/ws/phone/' + encodeURIComponent(room));
    ws.onopen = function () { tries = 0; clearInterval(pingT); pingT = setInterval(function () { if (ws.readyState === 1) ws.send('p'); }, 25000); };
    ws.onmessage = function (e) {
      var d = String(e.data);
      if (d === 'o') return;
      if (d.charAt(0) === 'E') { unseal(d).then(onSealed).catch(function () {}); return; }
      var m; try { m = JSON.parse(d); } catch (x) { return; }
      onPlain(m);
    };
    ws.onclose = function () {
      clearInterval(pingT); ready = false; sess = null; setDot();
      if (manual) return;
      waitMsg('연결하는 중');
      tries++; retryT = setTimeout(connect, Math.min(15000, 600 * tries));
    };
  }
  document.addEventListener('visibilitychange', function () { if (!document.hidden) connect(); });

  // ---- 연결 끊기 · 지우기 ----
  function sheet(on) { $('sheet').hidden = !on; }
  $('pw').addEventListener('click', function () { sheet(true); });
  $('sheet').addEventListener('click', function (e) {
    var a = e.target.getAttribute('data-s'); if (!a) return;
    sheet(false);
    if (a === 'off') disconnect('연결을 끊었습니다.', true);
    else if (a === 'forget') {
      if (!confirm('이 기기의 연결 정보를 지웁니다. 다시 쓰려면 PC의 QR을 다시 찍어야 합니다.')) return;
      send({ t: 'bye' });
      try { localStorage.removeItem('pair'); } catch (x) {}
      history.replaceState(null, '', location.pathname);
      $('pw').hidden = true;
      setTimeout(function () { disconnect('연결 정보를 지웠습니다.<br>홈 화면에 추가한 아이콘도 지워 주세요.', false); }, 150);
    }
  });
  $('again').addEventListener('click', function () { manual = false; tries = 0; waitMsg('연결하는 중'); connect(); });
  function disconnect(msg, canAgain) {
    manual = true; clearTimeout(retryT); clearInterval(pingT); ready = false;
    try { if (ws) ws.close(1000, 'bye'); } catch (x) {}
    setDot(); waitMsg(msg, canAgain);
  }
  window.addEventListener('online', connect);
  connect();

  // ---- 단추 ----
  var deckData = null, appData = null;
  function colsFor(el) {
    var w = el.clientWidth || window.innerWidth;
    return w >= 640 ? 5 : w >= 470 ? 4 : 3;
  }
  // 두 손가락이 닿은 뒤에는 잠깐 단추 누름을 막는다
  var noTapUntil = 0;
  function makeKey(k, colors, onPress) {
    var b = document.createElement('button'); b.type = 'button'; b.className = 'key';
    b.style.setProperty('--c', k.color || (colors && colors[k.cat]) || '#7d8597');
    var ic = document.createElement('span'); ic.className = 'ic'; ic.textContent = k.icon || '•';
    var lb = document.createElement('span'); lb.className = 'lb'; lb.textContent = k.label || '';
    b.appendChild(ic); b.appendChild(lb);
    b.addEventListener('click', function () {
      if (Date.now() < noTapUntil) return;
      buzz();
      if (!onPress()) toast('연결되지 않음');
    });
    return b;
  }
  function appKeyPress(i) { return function () { return send({ t: 'run', a: 1, i: i }); }; }

  function buildDeck(keepPage) {
    var d = deckData; if (!d) return;
    var pagesEl = $('pages'), dotsEl = $('pdots');
    var at = keepPage ? Math.round(pagesEl.scrollLeft / Math.max(1, pagesEl.clientWidth)) : 0;
    pagesEl.innerHTML = ''; dotsEl.innerHTML = '';
    var colors = d.colors || {}, cols = colsFor(pagesEl);
    var list = [];
    // 좁은 화면이면 앱 단추를 맨 앞 쪽으로
    var wide = document.body.classList.contains('wide');
    if (!wide && appData && appData.name && (appData.keys || []).length) list.push({ app: true, name: '지금: ' + appData.name, keys: appData.keys });
    (d.pages || []).forEach(function (pg, pi) { list.push({ name: pg.name, keys: pg.keys || [], pi: pi }); });
    list.forEach(function (pg, n) {
      var page = document.createElement('div'); page.className = 'page' + (pg.app ? ' apppage' : '');
      var h = document.createElement('h2'); h.textContent = pg.name || ''; page.appendChild(h);
      var grid = document.createElement('div'); grid.className = 'grid'; grid.style.setProperty('--cols', cols);
      pg.keys.forEach(function (k, ki) {
        grid.appendChild(makeKey(k, colors, pg.app ? appKeyPress(ki) : function () { return send({ t: 'run', p: pg.pi, i: ki }); }));
      });
      page.appendChild(grid); pagesEl.appendChild(page);
      var dot = document.createElement('i'); dotsEl.appendChild(dot);
    });
    dotsEl.style.display = list.length > 1 ? '' : 'none';
    at = Math.min(at, list.length - 1);
    pagesEl.scrollLeft = at * pagesEl.clientWidth;
    markDot();
    buildAppbar();
  }
  function buildAppbar() {
    var bar = $('appkeys'); bar.innerHTML = '';
    $('appname').textContent = appData && appData.name ? appData.name : '';
    if (appData && appData.keys) appData.keys.forEach(function (k, i) { bar.appendChild(makeKey(k, null, appKeyPress(i))); });
    show(cur);
  }
  function onApp(m) {
    var was = appData && appData.name;
    appData = m;
    var wide = document.body.classList.contains('wide');
    // 앱이 바뀌면(좁은 화면) 앱 단추 쪽으로 넘어간다
    buildDeck(!(m.name && m.name !== was && !wide));
  }
  function markDot() {
    var el = $('pages'), i = Math.round(el.scrollLeft / Math.max(1, el.clientWidth));
    Array.prototype.forEach.call($('pdots').children, function (d, k) { d.classList.toggle('on', k === i); });
  }
  $('pages').addEventListener('scroll', markDot, { passive: true });
  var rzT = 0;
  window.addEventListener('resize', function () {
    clearTimeout(rzT);
    rzT = setTimeout(function () { var w = document.body.classList.contains('wide'); layout(); if (deckData && (w !== document.body.classList.contains('wide') || true)) buildDeck(true); }, 150);
  });
  layout();

  // ---- 제스처: 손을 다 뗄 때 한 번만 보낸다 ----
  // 제스처 판: 한 손가락·두 손가락 모두. 단추 화면: 두 손가락만(한 손가락은 단추 누르기·쪽 넘기기).
  function center(ts) { var x = 0, y = 0; for (var i = 0; i < ts.length; i++) { x += ts[i].clientX; y += ts[i].clientY; } return { x: x / ts.length, y: y / ts.length }; }
  function gestures(el, padMode) {
    var g = null;
    el.addEventListener('touchstart', function (e) {
      var ts = e.touches;
      if (padMode || ts.length >= 2) e.preventDefault();
      if (ts.length >= 2) noTapUntil = Date.now() + 600;
      if (!g) g = { n: 0, t0: Date.now() };
      if (ts.length > g.n) { g.n = ts.length; g.start = center(ts); g.last = g.start; }
    }, { passive: false });
    el.addEventListener('touchmove', function (e) {
      if (!g) return;
      if (padMode || g.n >= 2) e.preventDefault();
      var ts = e.touches;
      if (ts.length === g.n) g.last = center(ts);
    }, { passive: false });
    el.addEventListener('touchend', function (e) {
      if (padMode || (g && g.n >= 2)) e.preventDefault();
      if (!g || e.touches.length > 0) return;
      var s = g; g = null;
      if (!padMode && s.n < 2) return;
      if (s.n >= 2) noTapUntil = Date.now() + 400;
      var r = decide(s);
      if (!r) return;
      buzz(); toast(r.label);
      if (!send({ t: 'g', g: r.g, n: r.n || 0 })) toast('연결되지 않음');
    }, { passive: false });
    el.addEventListener('touchcancel', function () { g = null; });
  }
  gestures($('padarea'), true);
  gestures($('deck'), false);
  gestures($('appbar'), false);
  // 아이폰 사파리의 두 손가락 확대 막기
  document.addEventListener('gesturestart', function (e) { e.preventDefault(); }, { passive: false });
  document.addEventListener('dblclick', function (e) { e.preventDefault(); }, { passive: false });

  document.querySelectorAll('[data-g]').forEach(function (b) {
    b.addEventListener('click', function () {
      buzz(); toast(b.textContent.trim());
      if (!send({ t: 'g', g: b.getAttribute('data-g'), n: 0 })) toast('연결되지 않음');
    });
  });

  function decide(s) {
    var dx = s.last.x - s.start.x, dy = s.last.y - s.start.y, dt = Date.now() - s.t0;
    var ax = Math.abs(dx), ay = Math.abs(dy), still = ax < 14 && ay < 14;
    if (s.n === 1) {
      // 한 손가락은 짧게 미는 것만(톡·천천히 끌기는 무시)
      if (dt > 900 || (ax < 50 && ay < 50)) return null;
      if (ax > ay) return dx < 0 ? { g: 'next', label: '다음 쪽' } : { g: 'prev', label: '이전 쪽' };
      return dy < 0 ? { g: 'pgdn', label: '아래로 넘기기' } : { g: 'pgup', label: '위로 넘기기' };
    }
    if (s.n === 2) {
      if (still && dt < 350) return { g: 'mute', label: '소리 끄기·켜기' };
      if (ax < 30 && ay < 30) return null;
      if (ay > ax) {
        var n = Math.max(1, Math.min(10, Math.round(ay / 36)));
        return dy < 0 ? { g: 'volup', n: n, label: '소리 +' + n } : { g: 'voldown', n: n, label: '소리 −' + n };
      }
      return dx < 0 ? { g: 'undo', label: '되돌리기' } : { g: 'redo', label: '다시 하기' };
    }
    return null;
  }

  // ---- 출결: 학생 이름은 PC 리모컨 폴더에만 있고, 폰에는 연결된 동안 메모리에만 둔다 ----
  var tt = null, att = { cls: null, p: '', list: [], marks: {}, memo: {}, dirty: false };
  function esc(t) { return String(t == null ? '' : t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function hm() { var n = new Date(); return ('0' + n.getHours()).slice(-2) + ':' + ('0' + n.getMinutes()).slice(-2); }
  function dayIdx() { var d = new Date().getDay(); return d >= 1 && d <= 5 ? d - 1 : -1; }
  function nowPeriod() {
    var now = hm(), r = '';
    ((tt && tt.periods) || []).forEach(function (pr) { if (now >= pr.s && now <= pr.e) r = pr.p; });
    return r;
  }
  function openAtt() { if (!send({ t: 'tt' })) toast('연결되지 않음'); }

  function onTT(m) {
    tt = m;
    var di = dayIdx(), box = $('tt'), now = hm(), pick = null;
    box.innerHTML = '';
    (m.periods || []).forEach(function (pr) {
      var c = di >= 0 ? (pr.d[di] || '') : '';
      var cls = c.split(/\s+/)[0];
      var b = document.createElement('button'); b.type = 'button';
      b.className = 'ttc' + (c ? '' : ' empty') + (now >= pr.s && now <= pr.e ? ' now' : '');
      b.dataset.p = pr.p; b.dataset.cls = cls;
      b.innerHTML = '<b>' + esc(pr.p) + '</b><span>' + esc(c || '·') + '</span>';
      if (c) {
        b.addEventListener('click', function () { selectClass(cls, pr.p); });
        if (!pick && now <= pr.e) pick = { cls: cls, p: pr.p };   // 지금 교시, 없으면 다음 교시
      }
      box.appendChild(b);
    });
    box.hidden = !(m.periods || []).length;
    var sel = $('cls');
    sel.innerHTML = '<option value="">반 고르기</option>' + (m.classes || []).map(function (c) { return '<option>' + esc(c) + '</option>'; }).join('');
    if (!(m.classes || []).length) {
      $('roster').innerHTML = '<p class="attmsg">PC의 <b>리모컨\\명렬표</b> 폴더에 반별 명렬표를 넣어 주세요.<br>파일 이름이 반 이름이 됩니다. 예: <b>1-3.csv</b> (번호,이름)</p>';
      return;
    }
    if (att.cls) { sel.value = att.cls; markChips(); }
    else if (pick && m.classes.indexOf(pick.cls) >= 0) selectClass(pick.cls, pick.p);
    else $('roster').innerHTML = '<p class="attmsg">반을 고르세요.</p>';
  }
  $('cls').addEventListener('change', function () {
    var v = this.value;
    if (!v) return;
    if (!selectClass(v, nowPeriod())) this.value = att.cls || '';
  });
  function markChips() {
    Array.prototype.forEach.call($('tt').children, function (b) { b.classList.toggle('sel', b.dataset.cls === att.cls && b.dataset.p === att.p); });
    $('attinfo').textContent = att.p ? att.p + '교시' : '';
  }
  function selectClass(cls, p) {
    if (att.dirty && !confirm('저장하지 않은 출결이 있습니다. 다른 반으로 바꿀까요?')) return false;
    att = { cls: cls, p: p || '', list: [], marks: {}, memo: {}, dirty: false };
    $('cls').value = cls; markChips(); attSum();
    $('roster').innerHTML = '<p class="attmsg">불러오는 중</p>';
    if (!send({ t: 'roster', cls: cls, p: att.p })) toast('연결되지 않음');
    return true;
  }
  function onRoster(m) {
    if (m.cls !== att.cls) return;
    att.list = m.list || []; att.marks = m.marks || {};
    var box = $('roster'); box.innerHTML = '';
    if (!att.list.length) { box.innerHTML = '<p class="attmsg">명렬표에 학생이 없습니다.</p>'; return; }
    att.list.forEach(function (st) {
      var no = String(st[0]), name = st[1] || '';
      var b = document.createElement('button'); b.type = 'button'; b.className = 'cell'; b.dataset.no = no;
      b.innerHTML = '<b>' + esc(no) + '</b><span>' + esc(name) + '</span><i></i>';
      paint(b);
      var lpT = 0, lp = false;
      function startLP() { lp = false; clearTimeout(lpT); lpT = setTimeout(function () { lp = true; buzz(); openMemo(no, name); }, 550); }
      function stopLP() { clearTimeout(lpT); }
      b.addEventListener('touchstart', startLP, { passive: true });
      b.addEventListener('touchend', stopLP); b.addEventListener('touchmove', stopLP); b.addEventListener('touchcancel', stopLP);
      b.addEventListener('mousedown', startLP); b.addEventListener('mouseup', stopLP); b.addEventListener('mouseleave', stopLP);
      b.addEventListener('contextmenu', function (e) { e.preventDefault(); });
      b.addEventListener('click', function () {
        if (lp) { lp = false; return; }
        var cur = att.marks[no];
        att.marks[no] = cur === '지각' ? '결석' : cur === '결석' ? undefined : '지각';
        if (!att.marks[no]) delete att.marks[no];
        att.dirty = true; buzz(); paint(b); attSum();
      });
      box.appendChild(b);
    });
    attSum();
  }
  function paint(b) {
    var st = att.marks[b.dataset.no];
    b.classList.toggle('late', st === '지각');
    b.classList.toggle('abs', st === '결석');
    b.classList.toggle('memo', !!att.memo[b.dataset.no]);
  }
  function attSum() {
    var l = 0, a = 0;
    for (var k in att.marks) { if (att.marks[k] === '지각') l++; else if (att.marks[k] === '결석') a++; }
    $('attsum').textContent = att.cls ? '지각 ' + l + ' · 결석 ' + a : '';
    var sv = $('attsave'); sv.disabled = !att.cls || !att.list.length; sv.classList.toggle('dirty', att.dirty);
  }
  $('attsave').addEventListener('click', function () {
    if (!att.cls) return;
    if (!send({ t: 'att', cls: att.cls, p: att.p, marks: att.marks })) toast('연결되지 않음');
  });

  // 메모: 길게 눌러 열기
  var memoNo = null;
  function openMemo(no, name) {
    memoNo = no;
    $('memoWho').textContent = no + '번 ' + name;
    $('memoText').value = '';
    $('memo').hidden = false;
  }
  $('memo').addEventListener('click', function (e) {
    var a = e.target.getAttribute('data-m'); if (!a) return;
    if (a === 'chip') { var t = $('memoText'); t.value = (t.value ? t.value + ', ' : '') + e.target.textContent; return; }
    if (a === 'save') {
      var text = $('memoText').value.trim();
      if (!text) { toast('메모가 비어 있습니다'); return; }
      if (!send({ t: 'memo', cls: att.cls, p: att.p, no: memoNo, text: text })) { toast('연결되지 않음'); return; }
      att.memo[memoNo] = true;
      var c = document.querySelector('.cell[data-no="' + memoNo + '"]'); if (c) paint(c);
    }
    $('memo').hidden = true;
  });

  // ---- 사진·파일 주고받기: 256KB 조각을 잠가서 차례로 보낸다(중계는 저장하지 않음) ----
  var CHUNK = 256 * 1024, MAXF = 100 * 1024 * 1024;
  var upQ = [], upBusy = false, upWait = null;
  function fmtSize(n) { return n >= 1048576 ? (n / 1048576).toFixed(1) + 'MB' : Math.max(1, Math.round(n / 1024)) + 'KB'; }
  function prog(text, frac) {
    $('fprog').hidden = text == null;
    if (text != null) { $('fprogText').textContent = text; $('fbar').style.width = Math.round((frac || 0) * 100) + '%'; }
  }
  function drained() {
    // 앞선 것을 다 잠가 보냈고, 브라우저 보내기 버퍼가 줄 때까지
    return chain.then(function () {
      return new Promise(function (res) {
        (function chk() { if (!ws || ws.readyState !== 1 || ws.bufferedAmount < 1048576) res(); else setTimeout(chk, 60); })();
      });
    });
  }
  function bytesB64(buf) { return toB64(new Uint8Array(buf)); }
  function queueFiles(list) {
    for (var i = 0; i < list.length; i++) {
      if (list[i].size > MAXF) { toast(list[i].name + ': 100MB까지 보낼 수 있습니다'); continue; }
      if (!list[i].size) continue;
      upQ.push(list[i]);
    }
    if (!upBusy) nextUp();
  }
  function nextUp() {
    var f = upQ.shift();
    if (!f) { upBusy = false; return; }
    upBusy = true;
    var id = Math.random().toString(36).slice(2, 12), n = Math.ceil(f.size / CHUNK), i = 0;
    var kind = /^image\//.test(f.type) ? 'photo' : 'file';
    var name = f.name || (kind === 'photo' ? '사진.jpg' : '파일');
    if (kind === 'photo' && /^image\.(jpe?g|png|heic)$/i.test(name)) name = 'photo_' + new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14) + name.slice(5);
    upWait = { id: id, name: name };
    if (!send({ t: 'fput', id: id, name: name, size: f.size, n: n, kind: kind })) { toast('연결되지 않음'); upQ = []; upBusy = false; prog(null); return; }
    prog(name + ' 보내는 중', 0);
    (function step() {
      if (!upWait || upWait.id !== id) return;          // 실패 답이 와서 멈춤
      if (i >= n) { send({ t: 'fend', id: id }); prog(name + ' 마무리하는 중', 1); return; }
      drained().then(function () { return f.slice(i * CHUNK, Math.min(f.size, (i + 1) * CHUNK)).arrayBuffer(); }).then(function (buf) {
        if (!upWait || upWait.id !== id) return;
        if (!send({ t: 'fchunk', id: id, i: i, d: bytesB64(buf) })) { upWait = null; toast('연결이 끊겼습니다'); upQ = []; upBusy = false; prog(null); return; }
        i++; prog(name + ' 보내는 중', i / n);
        step();
      }).catch(function () { upWait = null; toast('파일을 읽지 못했습니다'); upBusy = false; prog(null); nextUp(); });
    })();
  }
  function onFileDone(m) {
    if (!upWait || upWait.id !== m.id) return;
    upWait = null; toast(m.msg || (m.ok ? '저장했습니다' : '보내지 못했습니다'));
    prog(null); nextUp();
  }
  $('fcam').addEventListener('change', function () { queueFiles(this.files); this.value = ''; });
  $('fpick').addEventListener('change', function () { queueFiles(this.files); this.value = ''; });

  // PC → 폰: 리모컨\폰으로 폴더
  var downs = {};
  function openFiles() { if (!send({ t: 'flist' })) toast('연결되지 않음'); }
  $('frefresh').addEventListener('click', openFiles);
  function onFileList(m) {
    var box = $('flist'); box.innerHTML = '';
    if (!(m.files || []).length) { box.innerHTML = '<p class="attmsg">PC 알림 영역의 리모컨 아이콘 → <b>폰으로 보낼 파일</b> 폴더에 파일을 넣으면 여기에 나옵니다.</p>'; return; }
    m.files.forEach(function (f) {
      var row = document.createElement('div'); row.className = 'frow';
      row.innerHTML = '<div><b></b><span></span></div><button type="button">받기</button>';
      row.querySelector('b').textContent = f.name;
      row.querySelector('span').textContent = fmtSize(f.size) + ' · ' + f.time;
      var btn = row.querySelector('button');
      btn.addEventListener('click', function () {
        if (btn.dataset.url) return;                      // 이미 받음: 아래 링크로
        if (f.size > MAXF) { toast('100MB까지 받을 수 있습니다'); return; }
        if (!send({ t: 'fget', name: f.name })) { toast('연결되지 않음'); return; }
        downs[f.name] = { row: row, btn: btn, parts: [], got: 0 };
        btn.disabled = true; btn.textContent = '0%';
      });
      box.appendChild(row);
    });
  }
  function onFileData(m) {
    var d = downs[m.name]; if (!d) return;
    if (d.id && d.id !== m.id) return;
    d.id = m.id;
    if (!d.parts[m.i]) { d.parts[m.i] = fromB64(m.d); d.got++; }
    d.btn.textContent = Math.round(d.got / m.n * 100) + '%';
    if (d.got < m.n) return;
    var blob = new Blob(d.parts), url = URL.createObjectURL(blob);
    delete downs[m.name];
    // 받기가 끝나면 누르는 링크로 바꾼다(아이폰은 사람이 눌러야 저장됨)
    var a = document.createElement('a'); a.href = url; a.download = m.name; a.textContent = '저장'; a.className = 'fsave';
    d.btn.replaceWith(a);
    toast('받았습니다. 저장을 누르세요');
  }

})();
