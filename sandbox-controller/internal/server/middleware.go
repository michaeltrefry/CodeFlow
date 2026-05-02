package server

import (
	"net/http"
	"time"
)

// requireAllowedSubject runs the post-handshake subject allowlist check. The
// TLS layer has already verified the cert chain by the time we get here; this
// middleware adds the subject-pin check on top.
//
// If the request reaches here without a peer cert, something is wrong with the
// listener config (RequireAndVerifyClientCert should have rejected the
// handshake). We still defend with an explicit reject and a loud log line.
func (s *Server) requireAllowedSubject(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS == nil || len(r.TLS.PeerCertificates) == 0 {
			s.logger.Warn("request reached app layer without client cert; check TLS config",
				"remote", r.RemoteAddr, "path", r.URL.Path)
			writeError(w, http.StatusUnauthorized, "mtls_required", "client certificate required", "mtls.peer", "")
			return
		}
		cert := r.TLS.PeerCertificates[0]
		if err := s.verifier.Verify(cert); err != nil {
			s.logger.Info("rejected: subject not on allowlist",
				"subject", cert.Subject.String(), "path", r.URL.Path, "remote", r.RemoteAddr, "err", err.Error())
			writeError(w, http.StatusForbidden, "mtls_subject_not_allowed", "client subject is not on the allowlist", "mtls.allowlist", "")
			return
		}
		next.ServeHTTP(w, r)
	})
}

// statusRecorder captures the response status for request logging.
type statusRecorder struct {
	http.ResponseWriter
	status int
	bytes  int64
}

func (r *statusRecorder) WriteHeader(code int) {
	r.status = code
	r.ResponseWriter.WriteHeader(code)
}

func (r *statusRecorder) Write(b []byte) (int, error) {
	if r.status == 0 {
		r.status = http.StatusOK
	}
	n, err := r.ResponseWriter.Write(b)
	r.bytes += int64(n)
	return n, err
}

// requestLogging emits a structured log line per request. Subject is logged at
// a coarse "client identity" level — common name only, no full DN — to keep cert
// PII out of telemetry per docs/sandbox-executor.md §14.3.
func (s *Server) requestLogging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r)

		var subjectCN string
		if r.TLS != nil && len(r.TLS.PeerCertificates) > 0 {
			subjectCN = r.TLS.PeerCertificates[0].Subject.CommonName
		}

		s.logger.Info("request",
			"method", r.Method,
			"path", r.URL.Path,
			"status", rec.status,
			"bytes", rec.bytes,
			"duration_ms", time.Since(start).Milliseconds(),
			"client_cn", subjectCN,
			"remote", r.RemoteAddr,
		)
	})
}
