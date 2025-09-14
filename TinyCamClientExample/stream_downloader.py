#!/usr/bin/env python3

import argparse
import asyncio
import base64
import hashlib
import hmac
import json
import logging
import os
import struct
import time
from dataclasses import dataclass
from typing import Optional, Tuple, AsyncIterator

import httpx
import websockets
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


# ──────────────────────────────── Utility ────────────────────────────────
def safe_b64decode(s: str) -> bytes:
    if not isinstance(s, str):
        raise TypeError("base64 input must be str")
    t = (
        s.strip()
        .replace("\\u002B", "+")
        .replace("\\u003d", "=")
        .replace("\\u003D", "=")
        .replace("\\u002f", "/")
        .replace("\\u002F", "/")
    )
    t = t.replace(" ", "").replace("\n", "").replace("\r", "")
    t = t.replace("-", "+").replace("_", "/")
    m = len(t) % 4
    if m:
        t += "=" * (4 - m)
    return base64.b64decode(t)


def b64e(b: bytes) -> str:
    return base64.b64encode(b).decode("ascii")


def hmac_b64(message: str, key_b64: str) -> str:
    key = safe_b64decode(key_b64)
    sig = hmac.new(key, message.encode("utf-8"), hashlib.sha256).digest()
    return b64e(sig)


def hkdf_sha256(ikm: bytes, salt: bytes, info: bytes, length: int) -> bytes:
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    okm = b""
    t = b""
    block_index = 1
    while len(okm) < length:
        t = hmac.new(prk, t + info + bytes([block_index]), hashlib.sha256).digest()
        okm += t
        block_index += 1
    return okm[:length]


def join_ct_tag(ct: bytes, tag: bytes) -> bytes:
    return ct + tag


def be_u64(b: bytes) -> int:
    return struct.unpack(">Q", b)[0]


# ──────────────────────────────── DataClass ────────────────────────────────
@dataclass
class Keys:
    management_key_b64: Optional[str]
    access_key_b64: str

    @staticmethod
    def load(path: str) -> "Keys":
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        mgmt = data.get("managementKey")
        acc = data["accessKey"]
        return Keys(mgmt, acc)


# ──────────────────────────────── Client ────────────────────────────────
class TinyCamClient:
    def __init__(
        self,
        http_base: str,
        ws_url: str,
        keys: Keys,
        out_path: str,
        codec_hint: str = "vp9",
        logger: Optional[logging.Logger] = None,
        ws_timeout: float = 20.0,
    ) -> None:
        self.http_base = http_base.rstrip("/")
        self.ws_url = ws_url
        self.keys = keys
        self.out_path = out_path
        self.codec_hint = codec_hint
        self.log = logger or logging.getLogger("TinyCamClient")
        self.ws_timeout = ws_timeout

    # ───────── Management API ─────────
    async def start_server(self, force: bool = True) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /start")
            return (0, "skip")
        body = {"force": bool(force), "ts":str(time.ctime())}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            r = await client.post(
                f"{self.http_base}/start",
                headers={"X-TinyCam-Auth": sig, "Content-Type": "application/json"},
                content=json.dumps(body),
            )
            self.log.info("[/start] %s %s", r.status_code, r.text)
            return (r.status_code, r.text)
    
    async def stop_server(self, force: bool = True) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /stop")
            return (0, "skip")
        body = {"force": bool(force), "ts":str(time.ctime())}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            r = await client.post(
                f"{self.http_base}/stop",
                headers={"X-TinyCam-Auth": sig, "Content-Type": "application/json"},
                content=json.dumps(body),
            )
            self.log.info("[/stop] %s %s", r.status_code, r.text)
            return (r.status_code, r.text)

    async def apply_config(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /apply-config")
            return (0, "skip")
        sig = hmac_b64(json.dumps({"force": True, "ts":str(time.ctime())}), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            try:
                r = await client.post(
                    f"{self.http_base}/apply-config",
                    headers={"X-TinyCam-Auth": sig, "Content-Type": "application/json"},
                    content=json.dumps({"force": True}),
                )
                self.log.info("[/apply-config POST] %s %s", r.status_code, r.text)
                return (r.status_code, r.text)
            except Exception as e:
                self.log.error("POST /apply-config error: %s", e)
                return (0, str(e))

    async def get_devices(self):
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /device")
            return (0, "skip")
        sig = hmac_b64("", self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            r = await client.get(f"{self.http_base}/device",
                                 headers={"X-TinyCam-Auth": sig})
            return (r.status_code, r.text)

    # ───────── Streaming ─────────
    async def iter_plain_chunks(self) -> AsyncIterator[bytes]:
        """
        TinyCam WS에 연결해 **복호화된 비디오 바이트**를 yield.
        파일 I/O는 하지 않음. (쓰기 분리)
        """
        access_key = safe_b64decode(self.keys.access_key_b64)

        # 1) token/exp/cnonce
        exp = int(time.time()) + 60
        token = hmac_b64(f"stream:{exp}", self.keys.access_key_b64)
        client_nonce = os.urandom(16)
        cnonce_b64 = b64e(client_nonce)

        ws_url = f"{self.ws_url}?token={token}&exp={exp}&cnonce={cnonce_b64}"
        self.log.info("[connect] %s", ws_url)

        async with websockets.connect(
            ws_url,
            max_size=None,
            ping_interval=20,
            ping_timeout=20,
            close_timeout=10,
            open_timeout=self.ws_timeout,
        ) as ws:
            # 2) hello
            hello_msg = await ws.recv()
            if isinstance(hello_msg, bytes):
                raise RuntimeError("expected hello text frame, got binary")
            hello = json.loads(hello_msg)
            if hello.get("type") != "hello":
                raise RuntimeError(f"unexpected hello: {hello}")

            server_nonce = safe_b64decode(hello["snonce"])
            conn_b64 = hello["conn"]
            conn_id = safe_b64decode(conn_b64)  # 4B
            codec = str(hello.get("codec", self.codec_hint))
            w = int(hello["w"])
            h = int(hello["h"])
            fps = int(hello["fps"])
            exp_srv = int(hello["exp"])
            if exp_srv != exp:
                self.log.warning("server exp mismatch: %s != %s", exp_srv, exp)

            # 3) Session key/HKDF + AAD
            salt = client_nonce + server_nonce
            info = b"tinycam hkdf v1"
            session_key = hkdf_sha256(ikm=access_key, salt=salt, info=info, length=32)
            aesgcm = AESGCM(session_key)
            aad = f"{conn_b64}|{exp}|{codec}|{w}x{h}|{fps}".encode("utf-8")

            # 4) 수신 루프 (복호화만)
            last_counter = 0
            while True:
                msg = await ws.recv()
                if isinstance(msg, str):
                    self.log.debug("[text] %s", msg)
                    continue

                buf = memoryview(msg)
                if len(buf) < 28:
                    self.log.warning("short frame: len=%d", len(buf))
                    continue

                nonce = bytes(buf[:12])
                tag = bytes(buf[12:28])
                ct = bytes(buf[28:])

                if nonce[:4] != conn_id:
                    raise RuntimeError("nonce connId mismatch")
                counter = be_u64(nonce[4:12])
                if counter <= last_counter:
                    raise RuntimeError(f"counter not increasing ({counter} <= {last_counter})")
                last_counter = counter

                try:
                    plain = aesgcm.decrypt(nonce, join_ct_tag(ct, tag), aad)
                except Exception as e:
                    self.log.error("decrypt error: %s", e)
                    raise

                yield plain  # ← 파일 쓰기 없이 복호화된 데이터만 제공

    # ───────── 파일 저장 도우미 (쓰기 분리) ─────────
    async def stream_to_file(self, out_path: Optional[str] = None) -> None:
        """
        iter_plain_chunks()를 사용하여 파일에 저장.
        """
        target = out_path or self.out_path
        total = 0
        self.log.info("[write] %s", target)
        with open(target, "wb") as f:
            async for chunk in self.iter_plain_chunks():
                f.write(chunk)
                total += len(chunk)
                self.log.debug("[sink] total=%.2f MB", total / (1024 * 1024))
        self.log.info("[done] bytes=%d", total)

    # (선택) 하위호환: 이전 이름 유지
    async def stream_once(self) -> None:
        await self.stream_to_file(self.out_path)


# ──────────────────────────────── Execution Unit ────────────────────────────────
async def main_async(args):
    logging.basicConfig(
        level=logging.DEBUG if args.debug else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    log = logging.getLogger("tinycam_cli")

    keys = Keys.load(args.keys)

    client = TinyCamClient(
        http_base=args.http,
        ws_url=args.ws,
        keys=keys,
        out_path=args.out,
        codec_hint=args.codec_hint,
        logger=log,
    )

    if args.device:
        print(await client.get_devices())
        return
    if args.start:
        await client.start_server(force=True)
        return
    if args.stop:
        await client.stop_server(force=True)
        return
    if args.apply:
        await client.apply_config()
        return

    backoff = 1.0
    while True:
        try:
            # 파일 저장 모드
            await client.stream_to_file(args.out)
            backoff = 1.0
        except (websockets.ConnectionClosed, websockets.InvalidStatusCode) as e:
            log.warning("[stream] websocket closed: %s", e)
        except Exception as e:
            log.error("[stream] error: %s", e)
        await asyncio.sleep(min(backoff, 10.0))
        backoff = min(backoff * 2.0, 10.0)


def parse_args():
    p = argparse.ArgumentParser(description="TinyCam secure streaming client")
    p.add_argument('--device', action='store_true', help="query device list")
    p.add_argument("--http", default="http://127.0.0.1:8080",
                   help="HTTP base for management endpoints (default: %(default)s)")
    p.add_argument("--ws", default="ws://127.0.0.1:8080/stream",
                   help="WebSocket URL (default: %(default)s)")
    p.add_argument("--out", default="tinycam_out.webm",
                   help="output file path (decrypted) (default: %(default)s)")
    p.add_argument("--keys", default="keys.json",
                   help="keys file path (JSON: {managementKey, accessKey}) (default: %(default)s)")
    p.add_argument("--codec-hint", default="vp9",
                   help="codec hint if server hello lacks codec (default: %(default)s)")
    p.add_argument("--start", action="store_true",
                   help="call /start before streaming (requires managementKey)")
    p.add_argument("--stop", action="store_true",
                   help="call /stop after streaming (requires managementKey)")
    p.add_argument("--apply", action="store_true",
                   help="call /apply-config before streaming (requires managementKey)")
    p.add_argument("--debug", action="store_true",
                   help="enable debug logging (timings, etc.)")
    return p.parse_args()


def main():
    args = parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        logging.getLogger("tinycam_cli").info("interrupted; bye.")


if __name__ == "__main__":
    main()
