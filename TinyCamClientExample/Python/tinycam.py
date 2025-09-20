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

import ssl
import httpx
import websockets
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


# ──────────────────────────────── Utility ────────────────────────────────

def gen_ts(skew_sec: int = 0) -> int:
    return int(time.time()) + int(skew_sec)

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
        use_ssl: bool,
        codec_hint: str = "vp9",
        logger: Optional[logging.Logger] = None,
        ws_timeout: float = 20.0,
        inactivity_timeout: float = 60.0,
        first_frame_timeout: float = 60.0,
    ) -> None:
        self.http_base = http_base.rstrip("/")
        self.ws_url = ws_url
        self.keys = keys
        self.out_path = out_path
        self.codec_hint = codec_hint
        self.use_ssl = use_ssl
        self.log = logger or logging.getLogger("TinyCamClient")
        self.ws_timeout = ws_timeout

        self.inactivity_timeout = inactivity_timeout
        self.heartbeat_interval = int(inactivity_timeout/2)
        self.first_frame_timeout = first_frame_timeout

    # ───────── Management API ─────────
    async def start_server(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /start")
            return (0, "skip")
        body = {"ts":gen_ts()}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            r = await client.post(
                f"{self.http_base}/start",
                headers={"Authorization": sig, "Content-Type": "application/json"},
                content=json.dumps(body),
            )
            self.log.info("[/start] %s %s", r.status_code, r.text)
            return (r.status_code, r.text)
    
    async def stop_server(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /stop")
            return (0, "skip")
        body = {"ts":gen_ts()}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0, verify=False) as client:
            r = await client.post(
                f"{self.http_base}/stop",
                headers={"Authorization": sig, "Content-Type": "application/json"},
                content=json.dumps(body),
            )
            self.log.info("[/stop] %s %s", r.status_code, r.text)
            return (r.status_code, r.text)

    async def apply_config(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /apply-config")
            return (0, "skip")
        sig = hmac_b64(json.dumps({"ts":gen_ts()}), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0, verify=False) as client:
            try:
                r = await client.post(
                    f"{self.http_base}/apply",
                    headers={"Authorization": sig, "Content-Type": "application/json"},
                    content=json.dumps({"force": True}),
                )
                self.log.info("[/apply-config POST] %s %s", r.status_code, r.text)
                return (r.status_code, r.text)
            except Exception as e:
                self.log.error("POST /apply-config error: %s", e)
                return (0, str(e))

    async def get_devices(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /device")
            return (0, "skip")
        body = {"ts":gen_ts()}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0, verify=False) as client:
            r = await client.post(f"{self.http_base}/device",
                                 headers={"Authorization": sig},
                                 content=json.dumps(body))
            return (r.status_code, r.text)

    async def get_file_entry(self) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /start")
            return (0, "skip")
        body = {"ts":gen_ts()}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)
        async with httpx.AsyncClient(timeout=10.0) as client:
            r = await client.post(
                f"{self.http_base}/file/list",
                headers={"Authorization": sig, "Content-Type": "application/json"},
                content=json.dumps(body),
            )
            self.log.info("[/start] %s %s", r.status_code, r.text)
            return (r.status_code, r.text)
    
    async def get_file(self, remote, local_prefix, attachment:bool=True, chunk_size:int=1024*1024, resume: bool = True) -> Tuple[int, str]:
        if not self.keys.management_key_b64:
            self.log.warning("managementKey not set; skip /start")
            return (0, "skip")
        
        target = f"{local_prefix}{os.sep}{remote}"

        start = 0
        if resume and os.path.exists(target):
            start = os.path.getsize(target)
        
        body = {"name": remote, "attachment": bool(attachment), "ts": gen_ts()}
        sig = hmac_b64(json.dumps(body), self.keys.management_key_b64)

        url = f"{self.http_base}/file/download"
        headers = {
            "Authorization": sig,
            "Content-Type": "application/json",
            "Accept": "application/octet-stream",
        }
        if start > 0:headers["Range"] = f"bytes={start}-"

        async with httpx.AsyncClient(timeout=None) as client:
            r = await client.post(url, headers=headers, content=json.dumps(body))
            status = r.status_code

            if status in (401, 403, 404):
                return (status, r.text)
            if status in (304,):
                return (status, "not modified")

            if status not in (200, 206):
                return (status, r.text)

            mode = "ab" if (resume and start > 0 and status == 206) else "wb"
            received = 0
            with open(target, mode) as f:
                async for chunk in r.aiter_bytes(chunk_size):
                    if not chunk:
                        continue
                    f.write(chunk)
                    received += len(chunk)
            try:
                cr = r.headers.get("content-range")
                if cr and "/" in cr:
                    total = int(cr.split("/")[-1])
                    size = os.path.getsize(target)
                    if self.log and size != total:
                        self.log.warning("download size mismatch: %s != %s", size, total)
            except Exception:
                pass
            return (status, os.path.abspath(target))

    async def heartbeat_task(self, ws: websockets.WebSocketClientProtocol):
        while True:
            try:
                await asyncio.sleep(self.heartbeat_interval)
                await ws.send(json.dumps({"type": "ping", "ts": time.time()}))
                self.log.debug("[health] ping sent")
            except Exception as e:
                self.log.debug("[health] heartbeat end: %s", e)
                break

    async def watchdog_task(self, ws: websockets.WebSocketClientProtocol, last_rx_ts_getter):
        while True:
            try:
                await asyncio.sleep(1.0)
                if time.monotonic() - last_rx_ts_getter() > self.inactivity_timeout:
                    self.log.warning("[health] inactivity %.1fs → closing", self.inactivity_timeout)
                    try:
                        await ws.close(code=1001, reason="client watchdog inactivity")
                    finally:
                        break
            except Exception as e:
                self.log.debug("[health] watchdog end: %s", e)
                break

    # ───────── Streaming ─────────
    async def get_chunks(self) -> AsyncIterator[bytes]:

        if(self.use_ssl):
            sslctx = ssl.create_default_context()
            sslctx.check_hostname = False
            sslctx.verify_mode = ssl.CERT_NONE
        else:
            sslctx = None

        access_key = safe_b64decode(self.keys.access_key_b64)

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
            ssl=sslctx,
        ) as ws:
            try:
                hello_msg = await asyncio.wait_for(ws.recv(), timeout=self.ws_timeout)
            except asyncio.TimeoutError:
                raise RuntimeError("hello timeout")

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

            # 3) client → server: start
            start_payload = {"type": "start", "conn": conn_b64, "exp": exp_srv}
            await ws.send(json.dumps(start_payload))
            self.log.info("[start] sent")

            # 4) derive session key & AAD
            salt = client_nonce + server_nonce
            info = b"tinycam hkdf v1"
            session_key = hkdf_sha256(ikm=access_key, salt=salt, info=info, length=32)
            aesgcm = AESGCM(session_key)
            aad = f"{conn_b64}|{exp}|{codec}|{w}x{h}|{fps}".encode("utf-8")

            # 5) health: heartbeat + watchdog 시작
            last_rx_ts = time.monotonic()
            first_bin_deadline = last_rx_ts + self.first_frame_timeout
            first_bin_seen = False

            hb_task = asyncio.create_task(self.heartbeat_task(ws))
            wd_task = asyncio.create_task(self.watchdog_task(ws, lambda: last_rx_ts))

            try:
                while True:
                    msg = await ws.recv()
                    now = time.monotonic()
                    last_rx_ts = now

                    if isinstance(msg, str):
                        self.log.debug("[text] %s", msg)
                        continue

                    buf = memoryview(msg)
                    if len(buf) < 28:
                        self.log.warning("short frame: len=%d", len(buf))
                        continue

                    nonce = bytes(buf[:12])      # [0:12]
                    tag   = bytes(buf[12:28])    # [12:28]
                    ct    = bytes(buf[28:])      # [28:]

                    # connId(4B) + counter(8B, BE)
                    if nonce[:4] != conn_id:
                        raise RuntimeError("nonce connId mismatch")
                    counter = be_u64(nonce[4:12])

                    if first_bin_seen:
                        pass

                    try:
                        plain = aesgcm.decrypt(nonce, join_ct_tag(ct, tag), aad)
                    except Exception as e:
                        self.log.error("decrypt error: %s", e)
                        raise

                    if not first_bin_seen:
                        first_bin_seen = True
                    yield plain

                    if not first_bin_seen and now > first_bin_deadline:
                        raise RuntimeError("first binary frame timeout")
            finally:
                for t in (hb_task, wd_task):
                    try:
                        t.cancel()
                    except Exception:
                        pass

    # ───────── Save streaming data ─────────
    async def stream_to_file(self, out_path: Optional[str] = None) -> None:
        target = out_path or self.out_path
        total = 0
        self.log.info("[write] %s", target)
        with open(target, "wb") as f:
            async for chunk in self.get_chunks():
                f.write(chunk)
                total += len(chunk)
                self.log.debug("[sink] total=%.2f MB", total / (1024 * 1024))
        self.log.info("[done] bytes=%d", total)

# ──────────────────────────────── Execution Unit ────────────────────────────────
async def main_async(args):
    logging.basicConfig(
        level=logging.DEBUG if args.debug else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    log = logging.getLogger("tinycam_cli")

    keys = Keys.load(args.keys)

    http_base = f"{'https' if args.ssl else 'http'}://{args.host}:{args.port}"
    wss_base  = f"{'wss' if args.ssl else 'ws'}://{args.host}:{args.port}/stream"
    
    client = TinyCamClient(
        http_base=http_base,
        ws_url=wss_base,
        keys=keys,
        out_path=args.out,
        codec_hint=args.codec_hint,
        inactivity_timeout=args.timeout,
        logger=log,
        use_ssl=args.ssl
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
    if args.list:
        print(await client.get_file_entry())
        return
    if args.file:
        print(await client.get_file(args.remote, args.local, resume=args.resume))
        return
    if args.apply:
        await client.apply_config()
        return

    backoff = 1.0
    while True:
        try:
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
    p.add_argument(
        "-k", "--keys", required=True,
        help="Path to JSON with credentials: {\"managementKey\": \"...\", \"accessKey\": \"...\"} "
            "(default: %(default)s).")

    p.add_argument(
        "-t", "--host", default="127.0.0.1",
        help="Base hostname or IP for management endpoints (default: %(default)s).")

    p.add_argument(
        "-p", "--port", type=int, default=8080,
        help="HTTP server port for management endpoints (default: %(default)s).")

    p.add_argument(
        "-l", "--ssl", action="store_true",
        help="Use HTTPS (wss/https) for all requests instead of HTTP (ws/http).")

    p.add_argument(
        "-o", "--out", default="tinycam_out.webm",
        help="Output file path for the decrypted recording (default: %(default)s).")
    
    p.add_argument(
        "--timeout", default=60, help="Websocket health check timeout  (default: %(default)s).")

    p.add_argument(
        "--device", action="store_true",
        help="List available capture/compute devices on this host and exit.")
    
    p.add_argument(
        "--list", action="store_true",
        help="List of recorded files.")
    
    p.add_argument(
        "--file", action="store_true",
        help="Download a remote file.")
    
    p.add_argument(
        "--remote", help="Remote file")
    
    p.add_argument(
        "--local", help="Directory name to download file.")
    
    p.add_argument(
        "--resume", action="store_true",
        help="Resume downloaded file.")
    
    p.add_argument(
        "--start", action="store_true",
        help="Call /start on the node before streaming (requires managementKey).")

    p.add_argument(
        "--stop", action="store_true",
        help="Call /stop on the node after streaming completes (requires managementKey).")

    p.add_argument(
        "--apply", action="store_true",
        help="Call /apply-config on the node before streaming (requires managementKey).")

    p.add_argument(
        "--codec-hint", default="vp9",
        help="Preferred codec to request/use when the server does not advertise one "
            "(default: %(default)s).")

    p.add_argument(
        "--debug", action="store_true",
        help="Enable verbose debug logging (timings, request/response details).")

    return p.parse_args()


def main():
    args = parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        logging.getLogger("tinycam_cli").info("interrupted; bye.")


if __name__ == "__main__":
    """
    [ SAMPLE COMMAND TO TEST ]
    python tinycam.py --host 127.0.0.1 --port 8080 --ssl --keys keys.json 
    """
    
    main()