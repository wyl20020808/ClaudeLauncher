"""Serve only signed public launcher releases through the existing TCP tunnel."""
import http.server
import json
import os
from pathlib import Path
import re
import threading
import time
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent
PUBLIC = ROOT / 'public'


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
        name = path[len(prefix):] if path.startswith(prefix) else ''
        if name != 'latest.json' and not re.fullmatch(r'ClaudeLauncher-\d+\.\d+\.\d+\.\d+\.exe', name):
            self.send_error(404)
            return
        target = PUBLIC / name
        if target.is_symlink() or not target.is_file() or target.resolve().parent != PUBLIC.resolve():
            self.send_error(404)
            return
        try:
            with target.open('rb') as source:
                self.send_response(200)
                self.send_header('Content-Type', 'application/json' if name == 'latest.json' else 'application/octet-stream')
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
