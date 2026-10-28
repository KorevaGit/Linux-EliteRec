# Тестовый канал: HTTPS, Basic tester:test-pass, раскладка как у update.elitesip.vip.
import base64, http.server, ssl, sys, os
os.chdir(sys.argv[1])
class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.headers.get("Authorization") != "Basic " + base64.b64encode(b"tester:test-pass").decode():
            self.send_response(401); self.end_headers(); return
        super().do_GET()
    def log_message(self, *a): pass
server = http.server.ThreadingHTTPServer(("0.0.0.0", 8443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); context.load_cert_chain("/tmp/channel.crt", "/tmp/channel.key")
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
