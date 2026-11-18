"""A local replay target for the README walkthrough."""

from http.server import BaseHTTPRequestHandler, HTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/hook":
            self.send_error(404)
            return
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        print("event:", self.headers.get("X-Hookdesk-Event-Id"))
        print("body:", body.decode("utf-8"))
        self.send_response(202)
        self.end_headers()


if __name__ == "__main__":
    print("Listening on http://127.0.0.1:9001/hook")
    HTTPServer(("127.0.0.1", 9001), Handler).serve_forever()
