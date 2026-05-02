// Command sandbox-controller is the agent-driven container-job sandbox controller.
//
// As of sc-529, /run actually spawns sibling containers under gVisor (runsc).
// sc-530 adds the image whitelist, sc-531 adds workspace path validation,
// sc-538 lands the deploy-time hardening (distroless image, AppArmor, seccomp,
// cap_drop, no egress).
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
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/runner"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/server"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/whitelist"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/workspace"
)

// compileImageAllowlist adapts the config-shaped rules into a whitelist.Allowlist.
func compileImageAllowlist(cfg *config.Config) (*whitelist.Allowlist, error) {
	return compileImageAllowlistFromConfig(cfg)
}

func compileImageAllowlistFromConfig(cfg *config.Config) (*whitelist.Allowlist, error) {
	rules := make([]whitelist.Rule, 0, len(cfg.Images.Allowed))
	for _, r := range cfg.Images.Allowed {
		rules = append(rules, whitelist.Rule{
			Registry:   r.Registry,
			Repository: r.Repository,
			Tag:        r.Tag,
		})
	}
	return whitelist.Compile(rules)
}

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

	dockerClient := dockerd.New(cfg.Runner.DockerSocketPath)
	pingCtx, pingCancel := context.WithTimeout(context.Background(), 5*time.Second)
	if err := dockerClient.Ping(pingCtx); err != nil {
		// Don't os.Exit — the controller can still serve /healthz/version on a
		// host that's mid-deploy. /run will surface daemon_error per request,
		// which is the right behaviour to let monitoring pick it up.
		logger.Warn("docker daemon unreachable at startup; /run will fail until it recovers",
			"socket", cfg.Runner.DockerSocketPath, "err", err.Error())
	}
	pingCancel()

	imagePullTimeout := time.Duration(cfg.Runner.ImagePullTimeoutSeconds) * time.Second
	teardownTimeout := time.Duration(cfg.Runner.ContainerTeardownTimeoutSeconds) * time.Second
	jobRunner := runner.New(runner.NewRealDaemon(dockerClient), logger, imagePullTimeout, teardownTimeout)

	imageAllowlist, err := compileImageAllowlist(cfg)
	if err != nil {
		logger.Error("failed to compile image allowlist", "err", err)
		os.Exit(2)
	}

	wsValidator, err := workspace.New(cfg.Workspace.WorkdirRoot, cfg.Workspace.ResultsDirName)
	if err != nil {
		logger.Error("failed to compile workspace validator", "err", err)
		os.Exit(2)
	}

	build := server.BuildInfo{Commit: commit, BuildTime: buildTime, ConfigHash: cfg.Hash()}
	srv := server.New(cfg, verifier, build, logger, jobRunner, imageAllowlist, wsValidator)

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
		"allowed_images", imageAllowlist.Count(),
		"workdir_root", wsValidator.Root(),
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

	// SIGHUP reloads the image allowlist + logging level from the config file.
	// We deliberately do NOT reload TLS material or the listen address — those
	// would require restarting the listener; restart the process for those.
	hup := make(chan os.Signal, 1)
	signal.Notify(hup, syscall.SIGHUP)
	go func() {
		for range hup {
			logger.Info("SIGHUP received; reloading config", "path", *configPath)
			newCfg, err := config.Load(*configPath)
			if err != nil {
				logger.Error("config reload failed; keeping previous", "err", err)
				continue
			}
			newAllowlist, err := compileImageAllowlistFromConfig(newCfg)
			if err != nil {
				logger.Error("image-allowlist recompile failed; keeping previous", "err", err)
				continue
			}
			srv.SetAllowlist(newAllowlist)
			logger.Info("config reloaded",
				"config_hash", newCfg.Hash(),
				"allowed_images", newAllowlist.Count(),
			)
		}
	}()

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
