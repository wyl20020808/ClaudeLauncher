"""从本地程序读取第一组原始图标资源，保留全部尺寸，不修改像素。"""
import ctypes
from ctypes import wintypes
from pathlib import Path
import struct
import sys

source, destination = map(Path, sys.argv[1:3])
source = source.resolve()
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
kernel.LoadLibraryExW.restype = wintypes.HMODULE
kernel.FindResourceW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p]
kernel.FindResourceW.restype = ctypes.c_void_p
kernel.SizeofResource.argtypes = [wintypes.HMODULE, ctypes.c_void_p]
kernel.SizeofResource.restype = wintypes.DWORD
kernel.LoadResource.argtypes = [wintypes.HMODULE, ctypes.c_void_p]
kernel.LoadResource.restype = ctypes.c_void_p
kernel.LockResource.argtypes = [ctypes.c_void_p]
kernel.LockResource.restype = ctypes.c_void_p
kernel.FreeLibrary.argtypes = [wintypes.HMODULE]
callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
kernel.EnumResourceNamesW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, callback_type, ctypes.c_ssize_t]
module = kernel.LoadLibraryExW(str(source), None, 0x22)
if not module:
    raise ctypes.WinError(ctypes.get_last_error())
try:
    names = []
    @callback_type
    def collect(handle, kind, name, unused):
        names.append(name if name <= 0xffff else ctypes.wstring_at(name))
        return True
    if not kernel.EnumResourceNamesW(module, 14, collect, 0) or not names:
        raise RuntimeError('Executable has no icon group')

    def resource(kind, name):
        pointer = ctypes.c_void_p(name) if isinstance(name, int) else ctypes.cast(ctypes.c_wchar_p(name), ctypes.c_void_p)
        info = kernel.FindResourceW(module, pointer, ctypes.c_void_p(kind))
        if not info:
            raise ctypes.WinError(ctypes.get_last_error())
        size = kernel.SizeofResource(module, info)
        data = kernel.LockResource(kernel.LoadResource(module, info))
        if not data or not size:
            raise RuntimeError('Empty icon resource')
        return ctypes.string_at(data, size)

    group = resource(14, names[0])
    reserved, kind, count = struct.unpack_from('<HHH', group)
    assert reserved == 0 and kind == 1 and len(group) >= 6 + 14 * count
    directory = bytearray(group[:6])
    images = []
    offset = 6 + 16 * count
    sizes = []
    for index in range(count):
        width, height, colors, zero, planes, bits, size, identifier = struct.unpack_from('<BBBBHHIH', group, 6 + 14 * index)
        data = resource(3, identifier)
        assert len(data) == size
        directory.extend(struct.pack('<BBBBHHII', width, height, colors, zero, planes, bits, size, offset))
        images.append(data)
        sizes.append((width or 256, height or 256))
        offset += size
    destination.write_bytes(bytes(directory) + b''.join(images))
    print('Extracted original icon sizes:', sizes)
    for data in reversed(images):
        if data.startswith(b'\x89PNG\r\n\x1a\n'):
            destination.with_suffix('.preview.png').write_bytes(data)
            break
finally:
    kernel.FreeLibrary(module)
