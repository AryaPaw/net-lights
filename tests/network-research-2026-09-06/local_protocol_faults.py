"""Local-only controlled protocol failures; no certificate-store changes."""
import datetime as dt
import ipaddress
import json
from pathlib import Path
import shutil
import socket
import socketserver
import ssl
import subprocess
import tempfile
import threading
import time
import zipfile
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

ROOT=Path(__file__).resolve().parent
run=ROOT/('local-faults-'+dt.datetime.now().strftime('%Y%m%d-%H%M%S'))
run.mkdir()
scratch=Path(tempfile.mkdtemp(prefix='scratch-',dir=run))
key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'NetLights loopback test')])
now=dt.datetime.now(dt.timezone.utc)
cert=(x509.CertificateBuilder().subject_name(subject).issuer_name(subject)
      .public_key(key.public_key()).serial_number(x509.random_serial_number())
      .not_valid_before(now-dt.timedelta(minutes=5)).not_valid_after(now+dt.timedelta(days=1))
      .add_extension(x509.BasicConstraints(ca=True,path_length=None),critical=True)
      .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address('127.0.0.1'))]),critical=False)
      .sign(key,hashes.SHA256()))
certfile=scratch/'ca.pem';keyfile=scratch/'key.pem'
certfile.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
keyfile.write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(certfile,keyfile)

class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address=True
    daemon_threads=False

class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        sock=self.request
        sock.settimeout(3)
        try:
            if self.server.mode=='tls-stall':
                time.sleep(2.5);return
            if self.server.mode=='plain-on-tls':
                sock.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n');return
            with context.wrap_socket(sock,server_side=True) as tls:
                data=tls.recv(4096)
                if not data:return
                if self.server.mode=='http-stall':
                    time.sleep(2.5);return
                code={'ok':204,'missing':404,'limited':429,'untrusted':204}[self.server.mode]
                extra='Retry-After: 60\r\n' if code==429 else ''
                tls.sendall(f'HTTP/1.1 {code} Test\r\nContent-Length: 0\r\n{extra}Connection: close\r\n\r\n'.encode())
        except (OSError,ssl.SSLError):
            pass

rows=[]
for mode in ('ok','missing','limited','tls-stall','http-stall','plain-on-tls','untrusted'):
    with Server(('127.0.0.1',0),Handler) as server:
        server.mode=mode
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        port=server.server_address[1]
        with socket.create_connection(('127.0.0.1',port),timeout=1):tcp_ok=True
        args=[r'C:\Windows\System32\curl.exe','--head','--silent','--show-error','--max-time','1',
              '--output','NUL','--write-out','%{json}','--noproxy','*']
        if mode!='untrusted':args+=['--cacert',str(certfile)]
        args += [f'https://127.0.0.1:{port}/']
        p=subprocess.run(args,capture_output=True,timeout=3,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        r=json.loads(p.stdout.decode('utf-8','replace'))
        reachable=p.returncode==0 and 200<=r.get('http_code',0)<=599
        expected=mode in ('ok','missing','limited')
        expected_exit={'ok':0,'missing':0,'limited':0,'tls-stall':28,'http-stall':28,'plain-on-tls':35,'untrusted':60}[mode]
        row=dict(mode=mode,tcp_ok=tcp_ok,https_code=r.get('http_code'),curl_exit=p.returncode,
                 https_reachable=reachable,expected=expected,seconds=r.get('time_total'),
                 error=r.get('errormsg'),expected_exit=expected_exit,
                 passed=reachable==expected and p.returncode==expected_exit)
        rows.append(row);print(json.dumps(row),flush=True)
        server.shutdown();thread.join(timeout=2)
out=dict(scope='loopback only; TLS trust supplied per curl process, not installed in Windows',cases=rows,passed=all(r['passed'] for r in rows))
(run/'results.json').write_text(json.dumps(out,indent=2),encoding='utf-8')
with zipfile.ZipFile(run/'evidence.zip','w',zipfile.ZIP_DEFLATED) as z:
    z.write(run/'results.json','results.json')
    z.write(certfile,'test-public-certificate.pem')
with zipfile.ZipFile(run/'evidence.zip') as z:assert z.testzip() is None
assert scratch.resolve().parent==run.resolve() and scratch.name.startswith('scratch-')
shutil.rmtree(scratch)
assert out['passed'], 'Some controlled cases failed'
print('COMPLETE '+str(run))
