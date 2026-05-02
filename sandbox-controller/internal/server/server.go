// Package server hosts the HTTP API for the sandbox-controller.
package server

import (
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"log/slog"
	"net/http"
	"os"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/auth"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
)

// BuildInfo is surfaced by GET /version.
type BuildInfo struct {
	Commit     string `json:"commit"`
	BuildTime  string `json:"buildTime"`
	ConfigHash string `json:"configHash"`
}

// Server is the controller's HTTP server. Construct with New, mount with Handler.
type Server struct {
	cfg      *config.Config
	verifier *auth.SubjectAllowlist
	build    BuildInfo
	logger   *slog.Logger
}

// New returns a Server. It does not start the HTTP listener; the caller wires
// the returned Handler into an *http.Server with the TLSConfig from LoadTLSConfig.
func New(cfg *config.Config, verifier *auth.SubjectAllowlist, build BuildInfo, logger *slog.Logger) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	return &Server{
		cfg:      cfg,
		verifier: verifier,
		build:    build,
		logger:   logger,
	}
}

// Handler returns an http.Handler with the four endpoints mounted and the mTLS
// allowlist + structured-logging middleware applied.
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /run", s.handleRun)
	mux.HandleFunc("POST /cancel", s.handleCancel)
	mux.HandleFunc("GET /healthz", s.handleHealthz)
	mux.HandleFunc("GET /version", s.handleVersion)

	// Order: mTLS allowlist first (so unauthorized requests don't even hit logging
	// with a cert subject — they get logged at the rejection point with a clear
	// reason), then request logging.
	return s.requestLogging(s.requireAllowedSubject(mux))
}

// LoadTLSConfig builds the *tls.Config the http.Server uses. Mode is
// RequireAndVerifyClientCert: if a client doesn't present a cert chained to the
// configured client CA, the TLS handshake fails before the request reaches the
// HTTP layer at all. Plaintext on this listener is impossible.
func LoadTLSConfig(t config.TLSConfig) (*tls.Config, error) {
	cert, err := tls.LoadX509KeyPair(t.CertPath, t.KeyPath)
	if err != nil {
		return nil, fmt.Errorf("load server cert/key: %w", err)
	}

	caPEM, err := os.ReadFile(t.ClientCAPath)
	if err != nil {
		return nil, fmt.Errorf("read client CA bundle: %w", err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(caPEM) {
		return nil, fmt.Errorf("client CA bundle %q contained no usable certificates", t.ClientCAPath)
	}

	return &tls.Config{
		Certificates: []tls.Certificate{cert},
		ClientCAs:    pool,
		ClientAuth:   tls.RequireAndVerifyClientCert,
		MinVersion:   tls.VersionTLS13,
	}, nil
}
