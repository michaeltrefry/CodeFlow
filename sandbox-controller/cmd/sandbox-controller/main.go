// Command sandbox-controller is the agent-driven container-job sandbox controller.
//
// This is the scaffold from sc-528. The service exposes an mTLS-only HTTP API
// (POST /run, POST /cancel, GET /healthz, GET /version). For now /run echoes the
// validated request back; sc-529 wires it to dockerd+runsc, sc-530 adds the image
// whitelist, sc-531 adds workspace path validation. sc-538 adds the deploy-time
// hardening (distroless image, AppArmor, seccomp, cap_drop, no egress).
//
// See docs/sandbox-executor.md (sc-527) for the full architecture.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/apilog"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/auth"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/server"
)

// Build metadata, set via -ldflags="-X main.commit=... -X main.buildTime=...".
var (
	commit    = "dev"
	buildTime = "unknown"
)

func main() {
	configPath := flag.String("config", "/etc/cfsc/config.toml", "path to controller config file (TOML)")
	flag.Parse()

	logger := apilog.New(slog.LevelInfo)
	slog.SetDefault(logger)

	cfg, err := config.Load(*configPath)
	if err != nil {
		logger.Error("failed to load config", "path", *configPath, "err", err)
		os.Exit(2)
	}
	logger = apilog.New(cfg.Logging.SlogLevel())
	slog.SetDefault(logger)

	verifier, err := auth.NewSubjectAllowlist(cfg.TLS.AllowedClientSubjects)
	if err != nil {
		logger.Error("failed to compile mTLS subject allowlist", "err", err)
		os.Exit(2)
	}

	tlsConfig, err := server.LoadTLSConfig(cfg.TLS)
	if err != nil {
		logger.Error("failed to load TLS material", "err", err)
		os.Exit(2)
	}

	build := server.BuildInfo{Commit: commit, BuildTime: buildTime, ConfigHash: cfg.Hash()}
	srv := server.New(cfg, verifier, build, logger)

	httpServer := &http.Server{
		Addr:              cfg.Server.Listen,
		Handler:           srv.Handler(),
		TLSConfig:         tlsConfig,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      0, // /run can run for the configured per-job timeout
		IdleTimeout:       60 * time.Second,
		ErrorLog:          slog.NewLogLogger(logger.Handler(), slog.LevelWarn),
	}

	logger.Info("sandbox-controller starting",
		"listen", cfg.Server.Listen,
		"commit", commit,
		"build_time", buildTime,
		"allowed_subjects", verifier.Count(),
	)

	errCh := make(chan error, 1)
	go func() {
		// ListenAndServeTLS pulls cert/key from TLSConfig.Certificates; pass empty strings.
		if err := httpServer.ListenAndServeTLS("", ""); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errCh <- fmt.Errorf("listen failed: %w", err)
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)

	select {
	case sig := <-stop:
		logger.Info("shutdown signal received", "signal", sig.String())
	case err := <-errCh:
		logger.Error("server failed", "err", err)
		os.Exit(1)
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		logger.Error("graceful shutdown failed", "err", err)
		os.Exit(1)
	}
	logger.Info("sandbox-controller stopped")
}
