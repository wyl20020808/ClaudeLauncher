"""Mirror the latest signed releases from GitHub into public/ so either platform can publish on its own.

Only a manifest whose RSA signature verifies against a trusted public key, whose version is newer than the
one already served, and whose package matches its size and SHA-256 is ever switched in. latest.json (1.2.0
clients) is left to publish-release.ps1. Stdlib only; run `python sync.py` for a single pass.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import time
import urllib.request
from urllib.parse import urlsplit
import xml.etree.ElementTree as ElementTree

ROOT = Path(__file__).resolve().parent
PUBLIC = ROOT / 'public'
GITHUB = 'https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/'
REDIRECT_HOSTS = {'github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com'}
SIZE_LIMIT = 50 * 1024 * 1024
FILES = {'windows': 'ClaudeLauncherSetup-{}.exe', 'macos': 'ClaudeLauncher-{}.dmg'}
MINIMUM_OS = {'windows': r'[0-9]{1,5}(\.[0-9]{1,5}){0,2}', 'macos': r'[0-9]{1,3}(\.[0-9]{1,3}){0,2}'}
# 公钥文件（公开内容）。macOS 另信任 Mac 发布机自己的密钥，文件不存在时只用共用密钥。
KEYS = {
    'windows': [ROOT.parent / 'assets' / 'update-public-key.xml'],
    'macos': [ROOT.parent / 'assets' / 'update-public-key.xml', ROOT.parent.parent / 'resources' / 'update-public-key-macos.xml'],
}
SHA256_PREFIX = bytes.fromhex('3031300d060960864801650304020105000420')


def load_key(path):
    tree = ElementTree.parse(path)
    number = lambda tag: int.from_bytes(base64.b64decode(tree.findtext(tag)), 'big')
    return number('Modulus'), number('Exponent')


def rsa_verify(key, message, signature):
    """RSASSA-PKCS1-v1_5 with SHA-256."""
    modulus, exponent = key
    length = (modulus.bit_length() + 7) // 8
    if len(signature) != length:
        return False
    value = int.from_bytes(signature, 'big')
    if value >= modulus:
        return False
    digest = SHA256_PREFIX + hashlib.sha256(message).digest()
    expected = b'\x00\x01' + b'\xff' * (length - len(digest) - 3) + b'\x00' + digest
    return pow(value, exponent, modulus).to_bytes(length, 'big') == expected


def version_key(text):
    return tuple(int(part) for part in text.split('.'))


def verify(data, platform, keys):
    """Return the manifest dict when shape and signature are valid; raise ValueError otherwise."""
    if not data or len(data) > 16384:
        raise ValueError('manifest empty or too large')
    m = json.loads(data.decode('utf-8-sig'))
    version, size = m.get('version'), m.get('size')
    if (m.get('platform') != platform or not isinstance(version, str) or not re.fullmatch(r'[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}', version, re.ASCII)
            or m.get('file') != FILES[platform].format(version) or type(size) is not int or not 1024 <= size <= SIZE_LIMIT
            or not isinstance(m.get('sha256'), str) or not re.fullmatch(r'[a-f0-9]{64}', m['sha256'], re.ASCII)
            or not isinstance(m.get('minimumOS'), str) or not re.fullmatch(MINIMUM_OS[platform], m['minimumOS'], re.ASCII)
            or not isinstance(m.get('signature'), str)):
        raise ValueError('manifest shape invalid')
    payload = '{}\n{}\n{}\n{}\n{}\n'.format(platform, version, m['file'], size, m['sha256']).encode('utf-8')
    signature = base64.b64decode(m['signature'], validate=True)
    if not any(rsa_verify(key, payload, signature) for key in keys):
        raise ValueError('manifest signature invalid')
    return m


class GuardedRedirect(urllib.request.HTTPRedirectHandler):
    max_redirections = 5

    def redirect_request(self, request, fp, code, message, headers, url):
        parts = urlsplit(url)
        if parts.scheme != 'https' or (parts.hostname or '').lower() not in REDIRECT_HOSTS:
            raise ValueError('redirect to untrusted host')
        return super().redirect_request(request, fp, code, message, headers, url)


OPENER = urllib.request.build_opener(GuardedRedirect)


def fetch(url, limit, target=None, timeout=60):
    """Download url with a size cap; into memory, or streamed into target when given."""
    with OPENER.open(urllib.request.Request(url, headers={'User-Agent': 'ClaudeLauncher-Sync'}), timeout=timeout) as response:
        if target is None:
            data = response.read(limit + 1)
            if len(data) > limit:
                raise ValueError('response too large')
            return data
        total = 0
        with open(target, 'wb') as output:
            while chunk := response.read(65536):
                total += len(chunk)
                if total > limit:
                    raise ValueError('package larger than manifest')
                output.write(chunk)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as source:
        while chunk := source.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def sync_platform(platform, public=PUBLIC, base=GITHUB, keys=None):
    keys = keys or [load_key(p) for p in KEYS[platform] if p.is_file()]
    name = 'latest-{}.json'.format(platform)
    target = public / name
    data = fetch(base + name, 16384)
    remote = verify(data, platform, keys)
    if target.is_file():
        try:
            local = verify(target.read_bytes(), platform, keys)
        except ValueError:
            local = None
        if local and version_key(remote['version']) <= version_key(local['version']):
            return 'up to date ({})'.format(local['version'])
    package = public / remote['file']
    if package.is_file():
        if package.stat().st_size != remote['size'] or sha256_file(package) != remote['sha256']:
            raise ValueError(remote['file'] + ' already exists with different bytes')
    else:
        # 下载中的文件名不在服务白名单内，不会被客户端取到。
        partial = public / ('.' + remote['file'] + '.partial')
        try:
            fetch(base + remote['file'], remote['size'], partial, timeout=600)
            if partial.stat().st_size != remote['size'] or sha256_file(partial) != remote['sha256']:
                raise ValueError('package size or SHA-256 mismatch')
            os.replace(partial, package)
        finally:
            if partial.exists():
                partial.unlink()
    pending = public / (name + '.pending')
    pending.write_bytes(data)
    if target.exists():
        shutil.copyfile(target, public / (name + '.previous'))
    os.replace(pending, target)
    return 'switched to ' + remote['version']


def sync_once(public=PUBLIC, base=GITHUB):
    results = {}
    for platform in FILES:
        try:
            results[platform] = sync_platform(platform, public, base)
        except Exception as error:  # 单个平台失败不影响另一端，也不改动现有文件。
            results[platform] = 'error: {}'.format(error)
    (public.parent / 'sync-state.json').write_text(json.dumps({'checked': time.time(), 'results': results}, ensure_ascii=False), encoding='utf-8')
    return results


def run_forever(interval=600, delay=30):
    time.sleep(delay)
    while True:
        sync_once()
        time.sleep(interval)


if __name__ == '__main__':
    print(json.dumps(sync_once(), ensure_ascii=False))
