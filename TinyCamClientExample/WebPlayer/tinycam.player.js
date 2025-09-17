(function (root, factory) {
  if (typeof define === 'function' && define.amd) { define([], factory); }
  else if (typeof module === 'object' && module.exports) { module.exports = factory(); }
  else { root.TinyCamPlayer = factory(); }
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // ========= Utils =========
  const enc = new TextEncoder();

  function nowIso(){ return new Date().toISOString().replace('T',' ').replace('Z',''); }
  function toU8(x){
    if (x instanceof Uint8Array) return x;
    if (x instanceof ArrayBuffer) return new Uint8Array(x);
    if (ArrayBuffer.isView(x)) return new Uint8Array(x.buffer, x.byteOffset, x.byteLength);
    if (typeof x === 'string') return enc.encode(x);
    return enc.encode(String(x));
  }
  function b64ToBuf(b64) {
    b64 = (b64||'').trim()
      .replace(/\u002B/g,'+').replace(/\u003d/gi,'=').replace(/\u002f/gi,'/')
      .replace(/-/g,'+').replace(/_/g,'/');
    const pad = b64.length % 4; if (pad) b64 += '='.repeat(4 - pad);
    const bin = typeof atob !== 'undefined' ? atob(b64) : Buffer.from(b64, 'base64').toString('binary');
    const u8 = new Uint8Array(bin.length);
    for (let i=0;i<bin.length;i++) u8[i] = bin.charCodeAt(i);
    return u8.buffer;
  }
  function bufToB64(buf) {
    const u8 = toU8(buf);
    let bin=''; for (let i=0;i<u8.length;i++) bin += String.fromCharCode(u8[i]);
    if (typeof btoa !== 'undefined') return btoa(bin);
    return Buffer.from(bin, 'binary').toString('base64');
  }
  function randBytes(n){ const u=new Uint8Array(n); (crypto||window.crypto).getRandomValues(u); return u.buffer; }
  function u64beToBigInt(u8, off=0){ let v=0n; for (let i=0;i<8;i++){ v=(v<<8n)|BigInt(u8[off+i]); } return v; }

  async function importHmacKey(raw){
    const keyBuf = raw instanceof ArrayBuffer ? raw : toU8(raw).buffer;
    return crypto.subtle.importKey('raw', keyBuf, { name:'HMAC', hash:'SHA-256' }, false, ['sign']);
  }
  async function hmacSha256(keyRaw, data){
    const key = await importHmacKey(keyRaw);
    return crypto.subtle.sign('HMAC', key, toU8(data));
  }
  async function hkdfSha256(ikm, salt, info, length){
    const prkRaw = await hmacSha256(salt, ikm);
    const prk = await importHmacKey(prkRaw);

    const out = new Uint8Array(length);
    let t = new Uint8Array(0), written = 0, ctr = 1;
    while (written < length){
      const input = new Uint8Array(t.length + info.length + 1);
      input.set(t, 0); input.set(info, t.length); input[input.length-1] = ctr++;
      const block = new Uint8Array(await crypto.subtle.sign('HMAC', prk, input));
      const take = Math.min(block.length, length - written);
      out.set(block.subarray(0, take), written);
      written += take;
      t = block;
    }
    return out.buffer;
  }
  function makeAad(conn, exp, codec, w, h, fps){
    return enc.encode(`${conn}|${exp}|${codec}|${w}x${h}|${fps}`).buffer;
  }
  function mimeCandidates(codec){
    codec = (codec||'').toLowerCase(); const c=[];
    if (codec==='vp9' || codec==='vp09'){ c.push('video/webm; codecs="vp9"','video/webm; codecs="vp09.00.10.08"'); }
    else if (codec==='av1' || codec==='av01'){ c.push('video/webm; codecs="av1"','video/webm; codecs="av01"'); }
    else if (codec==='h264' || codec==='avc'){ c.push('video/mp4; codecs="avc1.42E01E"','video/mp4; codecs="avc1.4D401E"'); }
    else if (codec==='h265' || codec==='hevc'){ c.push('video/mp4; codecs="hvc1"','video/mp4; codecs="hev1"'); }
    else { c.push('video/webm; codecs="vp9"'); }
    return c;
  }
  function pickSupportedType(cands){ if (typeof MediaSource==='undefined') return null; for (const t of cands){ if (MediaSource.isTypeSupported(t)) return t; } return null; }

  // Storage helpers (localStorage with cookie fallback)
  function lsAvailable(){
    try { const k='__tc_ls_test__'; localStorage.setItem(k,'1'); localStorage.removeItem(k); return true; } catch { return false; }
  }
  function cookieSet(name, value, days=365){
    try {
      const maxAge = days*24*60*60;
      const secure = (typeof location!=='undefined' && location.protocol==='https:') ? '; Secure' : '';
      document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; Max-Age=${maxAge}; Path=/; SameSite=Lax${secure}`;
      return true;
    } catch { return false; }
  }
  function cookieGet(name){
    try{
      const n = encodeURIComponent(name) + '=';
      const parts = (document.cookie||'').split(/;\s*/);
      for (const p of parts){ if (p.startsWith(n)) return decodeURIComponent(p.slice(n.length)); }
    }catch{}
    return null;
  }
  function cookieDel(name){ try{ document.cookie = `${encodeURIComponent(name)}=; Max-Age=0; Path=/; SameSite=Lax`; }catch{} }
  function storageSetJSON(key, obj, days=365){
    const s = JSON.stringify(obj);
    if (lsAvailable()) { try { localStorage.setItem(key, s); return true; } catch {} }
    return cookieSet(key, s, days);
  }
  function storageGetJSON(key){
    if (lsAvailable()){
      try { const s = localStorage.getItem(key); if (s==null) return null; return JSON.parse(s); }
      catch {}
    }
    const c = cookieGet(key); if (!c) return null;
    try { return JSON.parse(c); } catch { return null; }
  }
  function storageDel(key){
    if (lsAvailable()) { try { localStorage.removeItem(key); } catch {} }
    cookieDel(key);
  }

  // ========= TinyCamPlayer =========
  class TinyCamPlayer {
    /**
     * @param {Object} opts
     * @param {HTMLVideoElement} opts.videoEl
     * @param {string} opts.host
     * @param {number} opts.port
     * @param {boolean} opts.ssl
     * @param {string} opts.accessKeyB64
     * @param {string} [opts.codecHint='vp9']
     * @param {number} [opts.serverTimeoutSec=60]
     * @param {boolean} [opts.debug=false]
     * @param {Function} [opts.onStatus]
     * @param {Function} [opts.onLog]
     * @param {Function} [opts.onError]
     * @param {Array}   [opts.logBuffer]
     * @param {number}  [opts.logBufferLimit=500]
     * @param {string}  [opts.storageKey='tinycam.player.v1']
     * @param {boolean} [opts.persistKey=false]
     * @param {boolean} [opts.autoLoad=false]
     * @param {boolean} [opts.autoSave=false]
     * @param {'grow'|'window'} [opts.bufferMode='grow']
     * @param {number}  [opts.windowMinutes=5]
     * @param {'mediarec'|'raw'} [opts.captureMode='mediarec']  // ★ default: mediarec
     */
    constructor(opts){
      if (!opts || !opts.videoEl) throw new Error('videoEl is required');
      this.video = opts.videoEl;
      this.host = opts.host || '127.0.0.1';
      this.port = typeof opts.port==='number' ? opts.port : 8080;
      this.ssl = !!opts.ssl;
      this.accessKeyB64 = opts.accessKeyB64 || '';
      this.codecHint = opts.codecHint || 'vp9';
      this.serverTimeoutSec = Math.max(2, +opts.serverTimeoutSec || 60);
      this.debug = !!opts.debug;

      this.onStatus = typeof opts.onStatus==='function' ? opts.onStatus : null;
      this.onLog = typeof opts.onLog==='function' ? opts.onLog : null;
      this.onError = typeof opts.onError==='function' ? opts.onError : null;

      this.logBuffer = Array.isArray(opts.logBuffer) ? opts.logBuffer : null;
      this.logBufferLimit = Number.isFinite(opts.logBufferLimit) ? Math.max(1, opts.logBufferLimit) : 500;

      this.storageKey = opts.storageKey || 'tinycam.player.v1';
      this.persistKey = !!opts.persistKey;
      this.autoLoad = !!opts.autoLoad;
      this.autoSave = !!opts.autoSave;

      this.bufferMode = (opts.bufferMode === 'window') ? 'window' : 'grow';
      this.windowMinutes = Number.isFinite(opts.windowMinutes) ? Math.max(1, opts.windowMinutes) : 5;

      // ★ Capture options
      this.captureMode = (opts.captureMode === 'raw') ? 'raw' : 'mediarec';

      // Internals
      this.ws = null; this.hbTimer=null; this.wdTimer=null; this.bufMon=null; this.trimTimer=null;
      this.lastRx = 0; this.firstBinarySeen=false; this.lastCounter = -1n;
      this.mediaSource=null; this.sourceBuffer=null; this.appendQueue=[];
      this.selectedType=null; this.containerMime='video/webm';

      this.aesKey=null; this.aadBuf=null; this.connId=null;

      // Raw capture store
      this.recording = false;
      this.captureParts = [];
      this.captureBytes = 0;
      this.recordMaxBytes = Infinity;
      this.captureMime = null;

      // MediaRecorder store
      this.mediaRecorder = null;
      this.recChunks = [];
      this._recStopPromise = null;

      this._recStartAt = null;
      this._recStopAt  = null;
      this._recBytes   = 0;

      this._boundHandlers = {
        onOpen: this._onOpen.bind(this),
        onMessage: this._onMessage.bind(this),
        onClose: this._onClose.bind(this),
        onError: this._onWsError.bind(this),
        onUpdateEnd: this._pump.bind(this),
      };

      if (this.autoLoad) {
        try { this.loadConfig(); } catch (e) { this._error(e); }
      }
    }

    // ---- Logging ----
    setDebug(flag){ this.debug = !!flag; }
    setStatus(s){ if (this.onStatus) try{ this.onStatus(s); }catch{} }
    _pushLogBuffer(line){
      if (!this.logBuffer || !this.debug) return;
      this.logBuffer.push(line);
      const over = this.logBuffer.length - this.logBufferLimit;
      if (over > 0) this.logBuffer.splice(0, over);
    }
    _log(...args){
      if (!this.debug) return;
      const line = `[${nowIso()}] ${args.map(a => typeof a==='string'?a:JSON.stringify(a)).join(' ')}`;
      this._pushLogBuffer(line);
      if (this.onLog) try{ this.onLog(line); }catch{} else console.log(line);
    }
    _error(e){ const err = e instanceof Error ? e : new Error(String(e)); if (this.onError) try{ this.onError(err); }catch{} else console.error(err); }

    // ---- Config persistence ----
    getConfig(opts={}) {
      const includeKey = !!(opts.includeKey ?? this.persistKey);
      const cfg = {
        host: this.host, port: this.port, ssl: this.ssl,
        codecHint: this.codecHint, serverTimeoutSec: this.serverTimeoutSec,
        debug: this.debug,
        bufferMode: this.bufferMode, windowMinutes: this.windowMinutes,
        captureMode: this.captureMode,
      };
      if (includeKey) cfg.accessKeyB64 = this.accessKeyB64;
      return cfg;
    }
    saveConfig(opts = {}) {
      const includeKey = !!(opts.includeKey ?? this.persistKey);
      const next = this.getConfig({ includeKey });
      if (!includeKey) {
        const prev = storageGetJSON(this.storageKey);
        if (prev && typeof prev.accessKeyB64 === 'string') {
        next.accessKeyB64 = prev.accessKeyB64;
        }
      }
      const ok = storageSetJSON(this.storageKey, next);
      if (!ok) throw new Error('Failed to persist config (storage blocked or full)');
      this._log('[cfg] saved', this.storageKey, includeKey ? '(with key)' : '(kept previous key)');
      return ok;
    }

    loadConfig(){
      const cfg = storageGetJSON(this.storageKey);
      if (!cfg) { this._log('[cfg] none'); return null; }
      if (typeof cfg.host==='string') this.host = cfg.host;
      if (typeof cfg.port==='number') this.port = cfg.port;
      if (typeof cfg.ssl==='boolean') this.ssl = cfg.ssl;
      if (typeof cfg.codecHint==='string') this.codecHint = cfg.codecHint;
      if (typeof cfg.serverTimeoutSec==='number' && cfg.serverTimeoutSec>=2) this.serverTimeoutSec = cfg.serverTimeoutSec;
      if (typeof cfg.debug==='boolean') this.debug = cfg.debug;
      if (typeof cfg.bufferMode==='string') this.bufferMode = (cfg.bufferMode==='window'?'window':'grow');
      if (typeof cfg.windowMinutes==='number' && cfg.windowMinutes>=1) this.windowMinutes = cfg.windowMinutes;
      if (typeof cfg.captureMode==='string') this.captureMode = (cfg.captureMode==='raw'?'raw':'mediarec');
      if (typeof cfg.accessKeyB64==='string' && this.persistKey) this.accessKeyB64 = cfg.accessKeyB64;
      this._log('[cfg] loaded', this.storageKey);
      return cfg;
    }
    clearConfig(){ storageDel(this.storageKey); this._log('[cfg] cleared', this.storageKey); }

    // ---- Buffer mode API ----
    setBufferMode(mode, windowMinutes){
      this.bufferMode = (mode==='window') ? 'window' : 'grow';
      if (Number.isFinite(windowMinutes) && windowMinutes>=1) this.windowMinutes = windowMinutes;
      this._log('[bufmode]', this.bufferMode, this.windowMinutes+'m');
    }
    setWindowMinutes(n){
      if (Number.isFinite(n) && n>=1) this.windowMinutes = n;
      this._log('[bufmode] windowMinutes=', this.windowMinutes);
    }

    // ---- Recorder helpers ----
    _pickRecorderMime(selectedType){
      if (typeof MediaRecorder === 'undefined') return null;
      const cands = [];
      const lower = (selectedType || '').toLowerCase();
      if (lower.includes('vp9')) cands.push('video/webm;codecs=vp9');
      if (lower.includes('av01') || lower.includes('av1')) cands.push('video/webm;codecs=av01.0.05M.08','video/webm;codecs=av01');
      cands.push('video/webm');
      for (const m of cands){
        try { if (MediaRecorder.isTypeSupported(m)) return m; } catch {}
      }
      return null;
    }

    // ---- Capture API ----
    startCapture(opts = {}) {
      this.recording = true;
      this.recordMaxBytes = Number.isFinite(opts.maxBytes) ? Math.max(1024*1024, opts.maxBytes) : Infinity;
      if (opts.mode) this.captureMode = (opts.mode === 'raw') ? 'raw' : 'mediarec';

      // ★ 통계 초기화
      this._recStartAt = performance.now();
      this._recStopAt  = null;
      this._recBytes   = 0;

      if (this.captureMode === 'mediarec' && typeof this.video.captureStream === 'function' && typeof MediaRecorder !== 'undefined') {
        const mime = opts.mime || this._pickRecorderMime(this.selectedType) || 'video/webm';
        this.captureMime = mime;
        try {
          const stream = this.video.captureStream();
          this.recChunks = [];
          this.mediaRecorder = new MediaRecorder(stream, { mimeType: mime });
          this.mediaRecorder.ondataavailable = (e) => {
            if (e.data && e.data.size) {
              this.recChunks.push(e.data);
              this._recBytes += e.data.size;
            }
          };
          this._recStopPromise = new Promise((resolve) => { this.mediaRecorder.onstop = () => resolve(); });
          this.mediaRecorder.start(1000); 
          this._log('[rec] mediarec start mime=', mime);
          return;
        } catch (e) {
          this._log('[rec] mediarec not available, fallback to raw:', e?.toString?.()||String(e));
          this.captureMode = 'raw';
        }
      }

      // raw fallback
      this.captureMime = opts.mime || (this.containerMime || 'application/octet-stream');
      this._log('[rec] raw start mime=', this.captureMime, 'maxBytes=', this.recordMaxBytes);
    }

    async stopCapture() {
      if (!this.recording) return;
      this.recording = false;

      this._recStopAt = performance.now();

      if (this.captureMode === 'mediarec' && this.mediaRecorder) {
        try { if (this.mediaRecorder.state !== 'inactive') this.mediaRecorder.stop(); } catch {}
        if (this._recStopPromise) { try { await this._recStopPromise; } catch {} }
        this._log('[rec] mediarec stop');
        return;
      }
      this._log('[rec] raw stop (note: raw may not be directly playable)');
    }

    clearCapture(){ this.recChunks = []; this.captureParts = []; this.captureBytes = 0; this._log('[rec] cleared'); }

    getCaptureStats() {
      const started = this._recStartAt;
      if (!started) return { recording:false, mode:this.captureMode, bytes:0, elapsedSec:0 };

      const end = this.recording ? performance.now() : (this._recStopAt || performance.now());
      const bytes = (this.captureMode === 'mediarec') ? (this._recBytes || 0) : (this.captureBytes || 0);
      const elapsedSec = Math.max(0, (end - started) / 1000);

      return { recording: !!this.recording, mode: this.captureMode, bytes, elapsedSec };
    }

    async getCaptureBlob(){
      if (this.captureMode === 'mediarec'){
        if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive'){
          try { this.mediaRecorder.stop(); } catch {}
          if (this._recStopPromise) { try { await this._recStopPromise; } catch {} }
        }
        const mime = this.captureMime || 'video/webm';
        return new Blob(this.recChunks, { type: mime });
      }
      const mime = this.captureMime || 'application/octet-stream';
      return new Blob(this.captureParts, { type: mime });
    }

    async downloadCapture(filename='tinycam.webm'){
      const blob = await this.getCaptureBlob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url; a.download = filename; document.body.appendChild(a);
      a.click();
      setTimeout(()=>{ URL.revokeObjectURL(url); document.body.removeChild(a); }, 100);
      this._log('[rec] downloaded', filename, (blob.size/1024/1024).toFixed(2)+'MB');
    }

    // ---- Public playback ----
    async start(){
      if (!this.accessKeyB64) throw new Error('accessKeyB64 required');
      if (this.ws && this.ws.readyState === WebSocket.OPEN) return;

      this.setStatus('connecting…');
      if (this.autoSave) { try { this.saveConfig(); } catch(e){ this._error(e); } }

      const accessKey = b64ToBuf(this.accessKeyB64);
      const exp = Math.floor(Date.now()/1000) + 60;
      const token = await hmacSha256(accessKey, `stream:${exp}`);
      const tokenB64 = bufToB64(token);
      this._cnonce = randBytes(16);
      const cnonceB64 = bufToB64(this._cnonce);

      const url = `${this.ssl?'wss':'ws'}://${this.host}:${this.port}/stream?token=${encodeURIComponent(tokenB64)}&exp=${exp}&cnonce=${encodeURIComponent(cnonceB64)}`;
      this._log('[connect]', url);

      this._hello = null;
      this.lastRx = performance.now();
      this.firstBinarySeen=false;
      this.lastCounter = -1n;
      this._helloTimer = setTimeout(()=>{ try{ this.ws?.close(1001,'hello timeout'); }catch{} }, 20000);

      this.ws = new WebSocket(url);
      this.ws.binaryType = 'arraybuffer';
      this.ws.addEventListener('open', this._boundHandlers.onOpen);
      this.ws.addEventListener('message', this._boundHandlers.onMessage);
      this.ws.addEventListener('close', this._boundHandlers.onClose);
      this.ws.addEventListener('error', this._boundHandlers.onError);
    }

    stop(){
      try { this.ws?.close(1000,'bye'); } catch {}
      this._stopHeartbeat(); this._stopWatchdog(); this._stopBufferMonitor(); this._stopTrimTimer();
      this._resetMse();
      this.setStatus('idle');
      if (this.autoSave) { try { this.saveConfig(); } catch(e){ this._error(e); } }
    }

    isRunning(){ return !!this.ws && this.ws.readyState === WebSocket.OPEN; }

    // ---- WS internals ----
    async _onOpen(){
      this._log('[ws] open');
      this._startHeartbeat();
      this._startWatchdog();
      if (this.debug) this._startBufferMonitor();
      this._startTrimTimer();
    }

    async _onMessage(ev){
      this.lastRx = performance.now();

      if (typeof ev.data === 'string'){
        if (!this._hello){
          let hello;
          try { hello = JSON.parse(ev.data); } catch { this._log('[hello] invalid json'); return; }
          if (hello.type !== 'hello'){ this._log('[hello] unexpected', hello); return; }
          clearTimeout(this._helloTimer);
          this._hello = hello;

          const serverNonceBuf = b64ToBuf(hello.snonce);
          const conn = String(hello.conn);
          const w = Number(hello.w|0), h = Number(hello.h|0), fps = Number(hello.fps|0);
          const codec = String(hello.codec || this.codecHint).toLowerCase();
          const expSrv = Number(hello.exp|0);
          if (!Number.isFinite(expSrv)) { this._log('[hello] invalid exp'); this.ws.close(); return; }

          this.connId = new Uint8Array(b64ToBuf(conn)).subarray(0,4);
          const cU8 = toU8(this._cnonce), sU8 = toU8(serverNonceBuf);
          const salt = new Uint8Array(cU8.length + sU8.length);
          salt.set(cU8, 0); salt.set(sU8, cU8.length);
          const accessKey = b64ToBuf(this.accessKeyB64);
          const info = enc.encode('tinycam hkdf v1');
          const sessionKey = await hkdfSha256(accessKey, salt.buffer, info, 32);
          this.aesKey = await crypto.subtle.importKey('raw', sessionKey, { name:'AES-GCM' }, false, ['decrypt']);
          this.aadBuf = makeAad(conn, expSrv, codec, w, h, fps);

          const type = pickSupportedType(mimeCandidates(codec));
          if (!type){ this._log('[mse] no supported type for codec:', codec); this.ws.close(1003,'unsupported'); return; }
          this.selectedType = type;
          this.containerMime = (type.split(';')[0] || 'video/webm');
          this._log('[mse] type:', type);
          try { await this._ensureMse(type); } catch(e){ this._log('[mse] init error', e?.toString?.()||String(e)); this.ws.close(); return; }

          this.ws.send(JSON.stringify({ type:'start', conn, exp: expSrv }));
          this._log('[start] sent');

          (async () => {
            const deadline = performance.now() + (this.serverTimeoutSec * 1000);
            while (!this.firstBinarySeen && this.ws && this.ws.readyState===WebSocket.OPEN && performance.now() < deadline) await new Promise(r=>setTimeout(r, 50));
            if (!this.firstBinarySeen && this.ws && this.ws.readyState===WebSocket.OPEN){
              this._log('[health] first binary frame timeout');
              try { this.ws.close(1001,'first frame timeout'); } catch {}
            }
          })();

          this.setStatus(`streaming ${w}x${h}@${fps} ${codec}`);
        } else {
          this._log('[text]', ev.data);
        }
        return;
      }

      try { await this._handleBinary(ev.data); }
      catch (e){ this._log('[ws] binary handle error:', e?.toString?.()||String(e)); }
    }

    _onClose(ev){
      this._log(`[ws] close: code=${ev.code} reason="${ev.reason}" wasClean=${ev.wasClean}`);
      this._stopHeartbeat(); this._stopWatchdog(); this._stopBufferMonitor(); this._stopTrimTimer();
      this.setStatus('closed');
      if (this.autoSave) { try { this.saveConfig(); } catch(e){ this._error(e); } }
    }
    _onWsError(){ this._error(new Error('WebSocket error')); }

    // ---- Binary/crypto ----
    async _handleBinary(arrBuf){
      const u8 = new Uint8Array(arrBuf);
      if (u8.length < 28){ this._log('[ws] short frame', u8.length); return; }

      const nonce = u8.subarray(0,12);
      const tag   = u8.subarray(12,28);
      const ct    = u8.subarray(28);

      if (this.connId && (nonce[0]!==this.connId[0] || nonce[1]!==this.connId[1] || nonce[2]!==this.connId[2] || nonce[3]!==this.connId[3])){
        this._log('[crypto] connId mismatch in nonce'); return;
      }
      const ctr = u64beToBigInt(u8, 4);
      if (this.lastCounter >= 0 && ctr <= this.lastCounter){
        this._log(`[crypto] non-increasing counter: ${ctr} <= ${this.lastCounter}`); return;
      }
      this.lastCounter = ctr;

      const ctTag = new Uint8Array(ct.length + 16);
      ctTag.set(ct, 0); ctTag.set(tag, ct.length);

      let plain;
      try{
        plain = await crypto.subtle.decrypt(
          { name:'AES-GCM', iv: nonce, additionalData: this.aadBuf, tagLength: 128 },
          this.aesKey,
          ctTag
        );
      }catch(e){
        this._log('[crypto] decrypt error:', e?.toString?.()||String(e)); return;
      }

      // raw capture only
      if (this.recording && this.captureMode === 'raw'){
        const seg = new Uint8Array(plain);
        this.captureParts.push(seg);
        this.captureBytes += seg.byteLength;
        while (this.captureBytes > this.recordMaxBytes && this.captureParts.length){
          const dropped = this.captureParts.shift();
          this.captureBytes -= dropped.byteLength;
        }
      }

      // debug fps
      if (this.debug){
        this._binCount = (this._binCount||0) + 1;
        const now = performance.now();
        if (!this._lastFpsLog) this._lastFpsLog = now - 2000;
        if (now - this._lastFpsLog > 2000){
          const fps = this._binCount * 1000 / (now - this._lastFpsLog);
          this._log(`[ws] binary fps~ ${fps.toFixed(1)}`);
          this._lastFpsLog = now; this._binCount = 0;
        }
      }

      this._enqueue(new Uint8Array(plain));
      if (!this.firstBinarySeen){
        this.firstBinarySeen = true;
        this._log('[stream] first decrypted frame appended');
        this.video.play().catch(()=>{});
        setTimeout(()=>{ 
          try { if (this.video.paused && this.video.buffered?.length) {
            const end = this.video.buffered.end(this.video.buffered.length-1);
            this.video.currentTime = Math.max(0, end - 0.1);
            this.video.play().catch(()=>{});
          }} catch {}
        }, 500);
      }
    }

    // ---- MSE & append ----
    _resetMse(){
      try { if (this.sourceBuffer && this.mediaSource?.readyState==='open') this.sourceBuffer.abort(); } catch {}
      try { if (this.mediaSource) this.mediaSource.endOfStream(); } catch {}
      this.sourceBuffer=null; this.mediaSource=null; this.appendQueue.length=0;
    }
    _enqueue(buf){ this.appendQueue.push(buf); this._maybeTrimBuffer(); this._pump(); }
    _pump(){
      if (!this.sourceBuffer || this.appendQueue.length===0 || this.sourceBuffer.updating) return;
      const chunk = this.appendQueue.shift();
      try{ this.sourceBuffer.appendBuffer(chunk); }
      catch(e){
        this._log('[mse] append error:', e?.toString?.()||String(e));
        if (e.name==='QuotaExceededError' && this.video.buffered?.length && !this.sourceBuffer.updating){
          const keep = Math.max(0, this.video.currentTime - 30);
          try { this.sourceBuffer.remove(0, keep); this.appendQueue.unshift(chunk); } catch {}
        }
      }
    }
    async _ensureMse(type){
      return new Promise((resolve,reject)=>{
        this._resetMse();
        this.mediaSource = new MediaSource();
        const url = URL.createObjectURL(this.mediaSource);
        this.video.src = url;
        this.mediaSource.addEventListener('sourceopen', ()=>{
          try{
            this.sourceBuffer = this.mediaSource.addSourceBuffer(type);
            this.sourceBuffer.mode='sequence';
            this.sourceBuffer.addEventListener('updateend', this._boundHandlers.onUpdateEnd);
            resolve();
          }catch(e){ reject(e); }
        }, { once:true });
      });
    }

    // ---- Buffer trimming (window mode) ----
    _maybeTrimBuffer(){
      if (this.bufferMode !== 'window') return;
      if (!this.sourceBuffer || this.sourceBuffer.updating) return;
      const br = this.video.buffered; if (!br || br.length===0) return;

      const winSec = this.windowMinutes * 60;
      const end = br.end(br.length - 1);
      const keepFrom = Math.max(0, end - winSec);
      const safety = 1.0; // margin
      const removeEnd = Math.max(0, keepFrom - safety);

      if (removeEnd > 0){
        try {
          this.sourceBuffer.remove(0, removeEnd);
          this._log('[trim] remove 0 ~', removeEnd.toFixed(2));
        } catch(e){
          this._log('[trim] error', e?.toString?.()||String(e));
        }
      }
    }
    _startTrimTimer(){ this._stopTrimTimer(); this.trimTimer = setInterval(()=>this._maybeTrimBuffer(), 1000); }
    _stopTrimTimer(){ if (this.trimTimer){ clearInterval(this.trimTimer); this.trimTimer=null; } }

    // ---- Health ----
    _startHeartbeat(){
      this._stopHeartbeat();
      const hbSec = Math.max(1, Math.floor(this.serverTimeoutSec / 2));
      this.hbTimer = setInterval(()=>{
        try { this.ws?.send(JSON.stringify({ type:'ping', ts: Date.now()/1000 })); } catch {}
      }, hbSec*1000);
      this._log(`[health] heartbeat every ${hbSec}s (server timeout ${this.serverTimeoutSec}s)`);
    }
    _stopHeartbeat(){ if (this.hbTimer){ clearInterval(this.hbTimer); this.hbTimer=null; } }

    _startWatchdog(){
      this._stopWatchdog();
      this.wdTimer = setInterval(()=>{
        const idle = (performance.now() - this.lastRx)/1000;
        if (idle > this.serverTimeoutSec){
          this._log(`[health] inactivity ${idle.toFixed(1)}s > ${this.serverTimeoutSec}s: closing`);
          try { this.ws?.close(1001, 'client watchdog inactivity'); } catch {}
          this._stopWatchdog();
        }
      }, 1000);
    }
    _stopWatchdog(){ if (this.wdTimer){ clearInterval(this.wdTimer); this.wdTimer=null; } }

    _startBufferMonitor(){
      if (!this.debug) return;
      this._stopBufferMonitor();
      this.bufMon = setInterval(()=>{
        try {
          const br = this.video.buffered;
          const ranges = [];
          for (let i=0;i<br.length;i++) ranges.push([br.start(i).toFixed(2), br.end(i).toFixed(2)]);
          this._log('[buf]', 'ct=', this.video.currentTime.toFixed(2), 'ranges=', JSON.stringify(ranges));
        } catch {}
      }, 2000);
    }
    _stopBufferMonitor(){ if (this.bufMon){ clearInterval(this.bufMon); this.bufMon=null; } }
  }

  return TinyCamPlayer;
}));
