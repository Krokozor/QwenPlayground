"""One-shot: convert CRLF -> LF for a file. Usage: python tools/normalize_lf.py <path>"""
import sys, pathlib
p = pathlib.Path(sys.argv[1])
data = p.read_bytes()
crlf = data.count(b'\r\n')
data = data.replace(b'\r\n', b'\n')
p.write_bytes(data)
print(f'{p}: {crlf} CRLF -> LF')
