"""Serve only signed public launcher releases through the existing TCP tunnel."""
import http.server
import json
import os
from pathlib import Path
import re
import threading
import time
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parent
PUBLIC = ROOT / 'public'
# 严格白名单：新协议清单与安装包，以及 1.2.0 客户端仍在使用的 latest.json 与单 exe。
ALLOWED = (
    (re.compile(r'latest(-windows|-macos)?\.json', re.ASCII), 'application/json'),
    (re.compile(r'ClaudeLauncherSetup-\d{1,4}\.\d{1,4}\.\d{1,4}\.exe', re.ASCII), 'application/octet-stream'),
    (re.compile(r'Claude多开启动器-\d{1,4}\.\d{1,4}\.\d{1,4}\.dmg', re.ASCII), 'application/x-apple-diskimage'),
    (re.compile(r'ClaudeLauncher-\d+\.\d+\.\d+\.\d+\.exe', re.ASCII), 'application/octet-stream'),
)


def content_type(name):
    for pattern, kind in ALLOWED:
        if pattern.fullmatch(name):
            return kind
    return None


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = 'LauncherUpdates/1'

    def log_message(self, *args):
        pass  # Do not retain customer IP addresses or query strings.

    def do_HEAD(self):
        self.serve(False)

    def do_GET(self):
        self.serve(True)

    def serve(self, body):
        path = urlsplit(self.path).path
        prefix = '/claude-launcher/'
        try:
            name = unquote(path[len(prefix):], errors='strict') if path.startswith(prefix) else ''
        except UnicodeDecodeError:
            name = ''
        kind = content_type(name)
        if kind is None:
            self.send_error(404)
            return
        target = PUBLIC / name
        if target.is_symlink() or not target.is_file() or target.resolve().parent != PUBLIC.resolve():
            self.send_error(404)
            return
        try:
            with target.open('rb') as source:
                self.send_response(200)
                self.send_header('Content-Type', kind)
                self.send_header('Content-Length', str(os.fstat(source.fileno()).st_size))
                self.send_header('Cache-Control', 'no-store')
                self.send_header('X-Content-Type-Options', 'nosniff')
                self.end_headers()
                if body:
                    while chunk := source.read(65536):
                        self.wfile.write(chunk)
        except (ConnectionError, TimeoutError, OSError):
            pass


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False
    slots = threading.BoundedSemaphore(16)

    def get_request(self):
        connection, address = super().get_request()
        connection.settimeout(15)
        return connection, address

    def process_request(self, request, client_address):
        if not self.slots.acquire(False):
            request.close()
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()


if __name__ == '__main__':
    try:
        with Server(('127.0.0.1', 8080), Handler) as server:
            (ROOT / 'server-state.json').write_text(json.dumps({'pid': os.getpid(), 'port': 8080, 'started': time.time()}), encoding='utf-8')
            server.serve_forever()
    except Exception as error:
        (ROOT / 'server-error.txt').write_text(str(error), encoding='utf-8')
        raise
