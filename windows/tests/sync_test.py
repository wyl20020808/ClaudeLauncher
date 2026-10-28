"""Offline checks for updates/sync.py: run `python -I tests/sync_test.py` from windows/. Uses a throwaway test key and file:// sources."""
import base64
import hashlib
import json
from pathlib import Path
import random
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'updates'))
import sync

checks = 0


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        raise AssertionError(message)


def prime(bits):
    while True:
        n = random.getrandbits(bits) | (1 << (bits - 1)) | 1
        if all(pow(a, n - 1, n) == 1 for a in (2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37)):
            return n


def test_key():
    e = 65537
    while True:
        p, q = prime(1024), prime(1024)
        phi = (p - 1) * (q - 1)
        if p != q and phi % e:
            return (p * q, e), pow(e, -1, phi)


def sign(key, d, payload):
    n = key[0]
    length = (n.bit_length() + 7) // 8
    digest = sync.SHA256_PREFIX + hashlib.sha256(payload).digest()
    em = b'\x00\x01' + b'\xff' * (length - len(digest) - 3) + b'\x00' + digest
    return pow(int.from_bytes(em, 'big'), d, n).to_bytes(length, 'big')


def manifest(key, d, platform, version, package, tamper=False):
    name = sync.FILES[platform].format(version)
    m = {'platform': platform, 'version': version, 'file': name, 'size': len(package), 'sha256': hashlib.sha256(package).hexdigest(),
         'minimumOS': '12.0' if platform == 'macos' else '10.0.19041'}
    payload = '{platform}\n{version}\n{file}\n{size}\n{sha256}\n'.format(**m).encode()
    m['signature'] = base64.b64encode(sign(key, d, payload + (b'x' if tamper else b''))).decode()
    return json.dumps(m).encode()


def publish(source, data, package, name, platform):
    (source / ('latest-' + platform + '.json')).write_bytes(data)
    (source / name).write_bytes(package)


def snapshot(folder):
    return {p.name: p.read_bytes() for p in folder.iterdir()}


def main():
    key, d = test_key()
    other, _ = test_key()
    with tempfile.TemporaryDirectory() as temp:
        source, public = Path(temp) / 'github', Path(temp) / 'site' / 'public'
        source.mkdir(); public.mkdir(parents=True)
        base = source.as_uri() + '/'
        dmg = random.randbytes(4096)
        data = manifest(key, d, 'macos', '1.7.4', dmg)
        publish(source, data, dmg, 'ClaudeLauncher-1.7.4.dmg', 'macos')

        check(sync.sync_platform('macos', public, base, [key]) == 'switched to 1.7.4', 'new release not switched in')
        check((public / 'latest-macos.json').read_bytes() == data and (public / 'ClaudeLauncher-1.7.4.dmg').read_bytes() == dmg, 'served files differ')
        check(not list(public.glob('.*partial')), 'partial file left behind')
        check(sync.sync_platform('macos', public, base, [key]).startswith('up to date'), 'same version re-synced')

        older = random.randbytes(2048)
        publish(source, manifest(key, d, 'macos', '1.7.3', older), older, 'ClaudeLauncher-1.7.3.dmg', 'macos')
        check(sync.sync_platform('macos', public, base, [key]).startswith('up to date'), 'downgraded')
        check((public / 'latest-macos.json').read_bytes() == data, 'downgrade changed manifest')

        before = snapshot(public)
        newer = random.randbytes(3000)
        rejected = [
            ('bad signature', manifest(key, d, 'macos', '1.7.5', newer, tamper=True), newer),
            ('untrusted key', manifest(other, d, 'macos', '1.7.5', newer), newer),
            ('wrong platform', manifest(key, d, 'windows', '1.7.5', newer), newer),
            ('package mismatch', manifest(key, d, 'macos', '1.7.5', newer), newer[:-1] + b'!'),
            ('package too long', manifest(key, d, 'macos', '1.7.5', newer), newer + b'extra'),
        ]
        for label, bad, package in rejected:
            publish(source, bad, package, 'ClaudeLauncher-1.7.5.dmg', 'macos')
            try:
                sync.sync_platform('macos', public, base, [key])
                check(False, label + ' accepted')
            except ValueError:
                pass
            check(snapshot(public) == before, label + ' changed public/')

        (public / 'ClaudeLauncher-1.7.5.dmg').write_bytes(b'different' * 200)
        before = snapshot(public)
        publish(source, manifest(key, d, 'macos', '1.7.5', newer), newer, 'ClaudeLauncher-1.7.5.dmg', 'macos')
        try:
            sync.sync_platform('macos', public, base, [key])
            check(False, 'overwrote existing package')
        except ValueError:
            pass
        check(snapshot(public) == before, 'existing package conflict changed public/')
        (public / 'ClaudeLauncher-1.7.5.dmg').unlink()

        check(sync.sync_platform('macos', public, base, [key]) == 'switched to 1.7.5', 'valid newer release not switched')
        check((public / 'latest-macos.json.previous').read_bytes() == data, 'previous manifest not kept')

        setup = random.randbytes(5000)
        publish(source, manifest(key, d, 'windows', '1.7.2', setup), setup, 'ClaudeLauncherSetup-1.7.2.exe', 'windows')
        check(sync.sync_platform('windows', public, base, [key]) == 'switched to 1.7.2', 'windows not synced')

    # 仓库里的正式公钥能解析，并能验证本机自建服务正在提供的正式清单（若存在）。
    real = sync.load_key(sync.KEYS['windows'][0])
    check(real[0].bit_length() == 3072, 'release key not RSA-3072')
    for platform in sync.FILES:
        served = sync.PUBLIC / ('latest-' + platform + '.json')
        if served.is_file():
            check(sync.verify(served.read_bytes(), platform, [real])['platform'] == platform, 'served manifest rejected')
    print('PASS: {} sync checks'.format(checks))


if __name__ == '__main__':
    main()
