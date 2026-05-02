// Package server hosts the HTTP API for the sandbox-controller.
package server

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"sync/atomic"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/auth"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/runner"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/whitelist"
)

// BuildInfo is surfaced by GET /version.
type BuildInfo struct {
	Commit     string `json:"commit"`
	BuildTime  string `json:"buildTime"`
	ConfigHash string `json:"configHash"`
}

// JobRunner is the contract /run uses. *runner.Runner satisfies it; tests
// inject a tiny stub implementation.
type JobRunner interface {
	Run(ctx context.Context, spec runner.JobSpec) (*runner.Result, error)
}

// Server is the controller's HTTP server. Construct with New, mount with Handler.
type Server struct {
	cfg       *config.Config
	verifier  *auth.SubjectAllowlist
	build     BuildInfo
	logger    *slog.Logger
	jobRunner JobRunner

	// allowlistRef is read atomically on every /run so SIGHUP can swap the
	// image policy without restarting the process (sc-530).
	allowlistRef atomic.Pointer[whitelist.Allowlist]
}

// allowlist returns the current Allowlist (may be nil, which is treated as
// default-deny by the handler).
func (s *Server) allowlist() *whitelist.Allowlist {
	return s.allowlistRef.Load()
}

// SetAllowlist atomically swaps the active image allowlist. Used on startup
// and again from the SIGHUP reload handler in main.
func (s *Server) SetAllowlist(a *whitelist.Allowlist) {
	s.allowlistRef.Store(a)
}

// New returns a Server. It does not start the HTTP listener; the caller wires
// the returned Handler into an *http.Server with the TLSConfig from LoadTLSConfig.
//
// jobRunner may be nil during scaffold-only deployments (sc-528). When nil,
// /run returns 503 service_unavailable. sc-529 onward always passes a runner.
//
// allowlist may be nil to opt out of layer-2 image-policy checks (test only —
// production code MUST pass a compiled allowlist; nil rejects every /run with
// image_not_allowed once sc-530 is in effect).
func New(
	cfg *config.Config,
	verifier *auth.SubjectAllowlist,
	build BuildInfo,
	logger *slog.Logger,
	jobRunner JobRunner,
	allowlist *whitelist.Allowlist,
) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	s := &Server{
		cfg:       cfg,
		verifier:  verifier,
		build:     build,
		logger:    logger,
		jobRunner: jobRunner,
	}
	s.SetAllowlist(allowlist)
	return s
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
