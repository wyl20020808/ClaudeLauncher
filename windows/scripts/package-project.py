"""Create a source project ZIP from an explicit allowlist; never package user state."""
from pathlib import Path
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TOP = 'Claude-MultiLauncher/'
FILES = ['README.md', 'AGENTS.md', '.gitignore', '.editorconfig', 'Build.cmd',
         'ClaudeMultiLauncher.sln', 'ClaudeLauncher.csproj', 'ClaudeLauncher.Tests.csproj',
         'updates/server.py', 'updates/start-server.ps1', 'updates/启动更新服务.cmd', 'updates/维护说明.md',
         'dist/Claude启动器-Windows.zip']
DIRECTORIES = ['src', 'assets', 'tests', 'scripts', 'docs']


def main():
    selected = [ROOT / name for name in FILES]
    for directory in DIRECTORIES:
        selected.extend(p for p in (ROOT / directory).rglob('*')
                        if p.is_file() and '__pycache__' not in p.parts)
    selected = sorted(set(selected))
    manifest = {}
    for path in selected:
        if path.is_symlink() or not path.resolve().is_relative_to(ROOT):
            raise ValueError('Refusing external link: ' + str(path))
        if path.suffix.lower() in {'.dpapi', '.exe', '.log'}:
            raise ValueError('Unexpected state/binary file: ' + str(path))
        manifest[path.relative_to(ROOT).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    destination = ROOT / 'dist/ClaudeLauncher-Project.zip'
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in selected:
            archive.write(path, TOP + path.relative_to(ROOT).as_posix())
        archive.writestr(TOP + 'PROJECT-MANIFEST.json', json.dumps(manifest, ensure_ascii=False, indent=2))
    with zipfile.ZipFile(destination) as archive:
        assert archive.testzip() is None
        for name, digest in manifest.items():
            assert hashlib.sha256(archive.read(TOP + name)).hexdigest() == digest
    print('PASS: project package,', len(manifest), 'files,', destination.stat().st_size, 'bytes')
    print(str(destination))


if __name__ == '__main__':
    main()
