package server

import (
	"net/http"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/trace"
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

// tracing extracts the W3C traceparent from CodeFlow's runner request and
// starts an inbound `controller.<route>` span. Downstream handlers and the
// runner inherit the span context via r.Context().
func (s *Server) tracing(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		propagator := otel.GetTextMapPropagator()
		ctx := propagator.Extract(r.Context(), propagation.HeaderCarrier(r.Header))
		tracer := s.telemetry.Tracer
		spanName := "controller." + sanitisePathForSpanName(r.URL.Path)
		ctx, span := tracer.Start(ctx, spanName,
			trace.WithSpanKind(trace.SpanKindServer),
			trace.WithAttributes(
				attribute.String("http.method", r.Method),
				attribute.String("http.route", r.URL.Path),
			),
		)
		defer span.End()

		var subjectCN string
		if r.TLS != nil && len(r.TLS.PeerCertificates) > 0 {
			subjectCN = r.TLS.PeerCertificates[0].Subject.CommonName
		}
		// Coarse identity attribute only — no full DN, no cert serial. §14.3.
		span.SetAttributes(attribute.String("cfsc.client_cn", subjectCN))

		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r.WithContext(ctx))
		span.SetAttributes(attribute.Int("http.status_code", statusOr200(rec.status)))
	})
}

// sanitisePathForSpanName turns "/run" into "run". Span names should be low-
// cardinality strings; we drop the leading slash and stop there.
func sanitisePathForSpanName(p string) string {
	if len(p) > 0 && p[0] == '/' {
		return p[1:]
	}
	return p
}

func statusOr200(s int) int {
	if s == 0 {
		return http.StatusOK
	}
	return s
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
