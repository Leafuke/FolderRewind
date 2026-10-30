"""Loopback-only WebDAV fixture for FolderRewind UI/UX acceptance."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, quote, urlparse
from xml.sax.saxutils import escape
import argparse, base64, email.utils, os, tempfile, time

parser = argparse.ArgumentParser()
parser.add_argument('--root', required=True)
parser.add_argument('--port', type=int, default=18627)
args = parser.parse_args()
root = Path(args.root).resolve()
if not (root / 'acceptance-owner').is_file():
    raise SystemExit('A marked acceptance directory is required.')
storage = root / 'webdav'
storage.mkdir(exist_ok=True)

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def handle(self):
        try: super().handle()
        except (ConnectionResetError, BrokenPipeError): pass
    def check(self):
        if self.path.endswith('/display.v1.json') and (root / 'webdav-display-delay').exists(): time.sleep(3)
        if (root / 'webdav-offline').exists():
            self.respond(503); return False
        expected = 'Basic ' + base64.b64encode(b'fixture:fixture-password').decode()
        if self.headers.get('Authorization') != expected:
            self.respond(401, headers={'WWW-Authenticate': 'Basic realm="acceptance"'}); return False
        return True
    def target(self):
        path = (storage / unquote(urlparse(self.path).path).lstrip('/')).resolve()
        if path != storage and storage not in path.parents: raise ValueError('Invalid path')
        return path
    def respond(self, status, content=b'', headers=None):
        self.send_response(status)
        self.send_header('Content-Length', str(len(content)))
        if status >= 400:
            self.close_connection=True
            self.send_header('Connection','close')
        for k,v in (headers or {}).items(): self.send_header(k,v)
        self.end_headers()
        if self.command != 'HEAD': self.wfile.write(content)
    def do_OPTIONS(self): self.respond(200, headers={'DAV': '1,2', 'Allow': 'OPTIONS,GET,HEAD,PUT,DELETE,MKCOL,PROPFIND,MOVE'})
    def do_PROPFIND(self):
        if not self.check(): return
        length=int(self.headers.get('Content-Length',0)); self.rfile.read(length)
        path=self.target()
        if not path.exists(): self.respond(404); return
        paths=[path]+(list(path.iterdir()) if path.is_dir() and self.headers.get('Depth') != '0' else [])
        def entry(p):
            href='/' + quote(p.relative_to(storage).as_posix()).replace('%2F','/')
            if p.is_dir() and not href.endswith('/'): href+='/'
            resource='<d:collection/>' if p.is_dir() else ''
            return f'<d:response><d:href>{escape(href)}</d:href><d:propstat><d:prop><d:resourcetype>{resource}</d:resourcetype><d:getcontentlength>{0 if p.is_dir() else p.stat().st_size}</d:getcontentlength><d:getlastmodified>{email.utils.formatdate(p.stat().st_mtime,usegmt=True)}</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>'
        content=('<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">' + ''.join(entry(p) for p in paths) + '</d:multistatus>').encode()
        self.respond(207,content,{'Content-Type':'application/xml; charset=utf-8'})
    def do_GET(self):
        if not self.check(): return
        path=self.target()
        if not path.is_file(): self.respond(404); return
        self.respond(200,path.read_bytes(),{'Last-Modified':email.utils.formatdate(path.stat().st_mtime,usegmt=True)})
    do_HEAD=do_GET
    def do_MKCOL(self):
        if not self.check(): return
        path=self.target()
        if path.exists(): self.respond(405); return
        path.mkdir(parents=True); self.respond(201)
    def do_PUT(self):
        length=int(self.headers.get('Content-Length',0)); content=self.rfile.read(length)
        if not self.check(): return
        path=self.target(); path.parent.mkdir(parents=True,exist_ok=True)
        if path.name == 'display.v1.json' and (root / 'webdav-display-denied').exists():
            self.respond(403); return
        with tempfile.NamedTemporaryFile(dir=path.parent,delete=False) as f: f.write(content); temporary=f.name
        os.replace(temporary,path); self.respond(201)
    def do_DELETE(self):
        if not self.check(): return
        path=self.target()
        if not path.exists(): self.respond(404); return
        if path.is_dir():
            # The app only deletes empty probe directories/files in this fixture.
            path.rmdir()
        else: path.unlink()
        self.respond(204)
    def log_message(self,fmt,*values):
        with (root / 'webdav.log').open('a',encoding='utf-8') as log: log.write((fmt % values)+'\n')

try: ThreadingHTTPServer(('127.0.0.1',args.port),Handler).serve_forever()
except KeyboardInterrupt: pass
