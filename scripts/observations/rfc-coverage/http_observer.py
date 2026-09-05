#!/usr/bin/env python3
"""Loopback-only payload-size observer for disposable benchmark traffic."""
import http.client
import http.server
import threading
import time
import urllib.parse


class HttpObservationProxy:
    def __init__(self, state):
        self.original = state
        self.records = []
        self.lock = threading.Lock()

    def __enter__(self):
        target = urllib.parse.urlparse(self.original['url'])
        if target.scheme != 'http' or target.hostname != '127.0.0.1':
            raise ValueError('The observer accepts only its disposable loopback HTTP fixture.')
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def log_message(self, *_):
                pass

            def forward(self):
                start = time.time_ns()
                body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
                headers = {k: v for k, v in self.headers.items()
                           if k.lower() not in ('connection', 'transfer-encoding', 'expect')}
                connection = http.client.HTTPConnection(target.hostname, target.port, timeout=30)
                try:
                    connection.request(self.command, self.path, body=body or None, headers=headers)
                    response = connection.getresponse()
                    data = response.read(8*1024*1024+1)
                    if len(data) > 8*1024*1024:
                        raise RuntimeError('The observation response exceeded its byte budget.')
                    record = dict(timestamp_ns=start, method=self.command, status=response.status,
                                  request_body_bytes=len(body), response_body_bytes=len(data),
                                  elapsed_ms=(time.time_ns()-start)/1e6)
                    with owner.lock:
                        owner.records.append(record)
                    self.send_response(response.status)
                    for key, value in response.getheaders():
                        if key.lower() not in ('content-length', 'transfer-encoding', 'connection'):
                            self.send_header(key, value)
                    self.send_header('Content-Length', str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                    self.wfile.flush()
                finally:
                    connection.close()

            do_GET = do_HEAD = do_OPTIONS = do_PROPFIND = do_PROPPATCH = forward
            do_REPORT = do_PUT = do_DELETE = do_MKCALENDAR = do_MKCOL = do_MOVE = forward

        self.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        origin = 'http://127.0.0.1:' + str(self.server.server_address[1])
        self.state = dict(self.original, url=origin,
                          caldav_url=self.original.get('caldav_url', self.original['url']).replace(self.original['url'], origin, 1))
        return self

    def snapshot(self):
        with self.lock:
            return len(self.records)

    def since(self, start):
        with self.lock:
            return self.records[start:].copy()

    def __exit__(self, *_):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
