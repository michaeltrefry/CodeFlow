// Package sweeper periodically removes orphaned containers and stale per-job
// results directories on the executor host (sc-533).
//
// Per-job teardown is synchronous to /run (sc-529's runner removes the
// container before responding) and the per-job tmpfs scratch is reclaimed by
// the kernel on container exit. The sweeper handles two residual cases:
//
//   - Containers labelled cf-managed=true that are older than the configured
//     TTL. These exist only when the controller crashed mid-job or a teardown
//     failed silently. With the TTL gate (default 24h) we never touch a job
//     that's still legitimately running.
//
//   - .results/{jobId}/ subdirectories under each {workdirRoot}/{traceId}/
//     that are older than the configured TTL (default 7d). These accumulate
//     because the artifact manifest only references files; the api/worker is
//     responsible for downloading what it cares about within the TTL window.
package sweeper

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// Daemon is the subset of dockerd.Client the sweeper uses. Defined here so
// tests can stub without standing up an httptest daemon.
type Daemon interface {
	ListContainers(ctx context.Context, labelFilter string, all bool) ([]dockerd.ContainerListItem, error)
	RemoveContainer(ctx context.Context, id string, force bool) error
}

// Config configures one Sweeper instance.
type Config struct {
	// Interval between sweep cycles. Defaults to 30 minutes.
	Interval time.Duration

	// ContainerTTL: containers labelled cf-managed=true older than this are
	// removed. Should be larger than any per-job timeout the controller will
	// honour (default 24h is well above any reasonable job timeout).
	ContainerTTL time.Duration

	// ResultsTTL: per-job results directories older than this are removed.
	// Default 7 days.
	ResultsTTL time.Duration

	// WorkdirRoot is the canonical workspace root the controller is configured
	// against. Same value as the workspace.Validator's root.
	WorkdirRoot string

	// ResultsDirName is the per-trace results subdir name (e.g. ".results").
	// Must match the value passed to workspace.New.
	ResultsDirName string
}

// Defaults applied by New if the corresponding Config field is zero.
const (
	DefaultInterval     = 30 * time.Minute
	DefaultContainerTTL = 24 * time.Hour
	DefaultResultsTTL   = 7 * 24 * time.Hour
)

// Result is the per-cycle summary the sweeper logs at info level.
type Result struct {
	ContainersRemoved int
	ResultsDirsRemoved int
	Errors            int
}

// Sweeper runs the periodic loop.
type Sweeper struct {
	daemon Daemon
	cfg    Config
	logger *slog.Logger

	mu              sync.Mutex
	daemonUnhealthy bool // set when ListContainers fails; cleared on next success
	now             func() time.Time
}

// New constructs a Sweeper. workdirRoot may be empty; ResultsDirsRemoved stays
// zero in that case (controller deployments without a workspace mount don't
// have results dirs to sweep).
func New(daemon Daemon, cfg Config, logger *slog.Logger) *Sweeper {
	if cfg.Interval == 0 {
		cfg.Interval = DefaultInterval
	}
	if cfg.ContainerTTL == 0 {
		cfg.ContainerTTL = DefaultContainerTTL
	}
	if cfg.ResultsTTL == 0 {
		cfg.ResultsTTL = DefaultResultsTTL
	}
	if cfg.ResultsDirName == "" {
		cfg.ResultsDirName = ".results"
	}
	if logger == nil {
		logger = slog.Default()
	}
	return &Sweeper{
		daemon: daemon,
		cfg:    cfg,
		logger: logger,
		now:    time.Now,
	}
}

// Run blocks until ctx is done. Logs one info line per non-empty cycle.
func (s *Sweeper) Run(ctx context.Context) {
	t := time.NewTicker(s.cfg.Interval)
	defer t.Stop()

	// Don't fire immediately on startup — give the daemon a moment to come up.
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			s.RunOnce(ctx)
		}
	}
}

// RunOnce performs a single sweep cycle synchronously. Exposed for tests and
// for an "ops on-demand sweep" path if we ever want one.
func (s *Sweeper) RunOnce(ctx context.Context) Result {
	res := Result{}

	containerCount, err := s.sweepContainers(ctx)
	res.ContainersRemoved = containerCount
	if err != nil {
		res.Errors++
		s.logger.Warn("container sweep failed", "err", err.Error())
	}

	if s.cfg.WorkdirRoot != "" {
		dirCount, err := s.sweepResultsDirs()
		res.ResultsDirsRemoved = dirCount
		if err != nil {
			res.Errors++
			s.logger.Warn("results-dir sweep failed", "err", err.Error())
		}
	}

	if res.ContainersRemoved > 0 || res.ResultsDirsRemoved > 0 {
		s.logger.Info("sweep cycle removed stale resources",
			"containers_removed", res.ContainersRemoved,
			"results_dirs_removed", res.ResultsDirsRemoved,
			"errors", res.Errors,
		)
	}
	return res
}

// sweepContainers lists cf-managed containers and removes those older than
// ContainerTTL. Returns count + error.
func (s *Sweeper) sweepContainers(ctx context.Context) (int, error) {
	const managedLabel = "cf-managed=true"

	items, err := s.daemon.ListContainers(ctx, managedLabel, true)
	if err != nil {
		// Pause: log once, expect recovery on the next tick.
		s.mu.Lock()
		first := !s.daemonUnhealthy
		s.daemonUnhealthy = true
		s.mu.Unlock()
		if first {
			s.logger.Info("docker daemon unreachable; sweep paused", "err", err.Error())
		}
		return 0, err
	}

	// Daemon recovered.
	s.mu.Lock()
	if s.daemonUnhealthy {
		s.logger.Info("docker daemon recovered; sweep resuming")
	}
	s.daemonUnhealthy = false
	s.mu.Unlock()

	cutoff := s.now().Add(-s.cfg.ContainerTTL)
	removed := 0
	for _, item := range items {
		if item.CreatedAt().After(cutoff) {
			continue // not stale yet
		}
		if err := s.daemon.RemoveContainer(ctx, item.ID, true); err != nil && !dockerd.IsNotFound(err) {
			s.logger.Warn("container remove failed during sweep",
				"id", item.ID, "labels", strings.Join(labelKeyValues(item.Labels), ","),
				"err", err.Error())
			continue
		}
		removed++
	}
	return removed, nil
}

// sweepResultsDirs walks the workdir root and removes per-job results dirs
// older than ResultsTTL. Layout: {workdirRoot}/{traceId}/{resultsDir}/{jobId}/.
// Path validation is deliberately strict — only directories at the expected
// depth get touched; anything else is ignored.
func (s *Sweeper) sweepResultsDirs() (int, error) {
	resolvedRoot, err := filepath.EvalSymlinks(s.cfg.WorkdirRoot)
	if err != nil {
		return 0, fmt.Errorf("results sweep: resolve workdir root: %w", err)
	}

	cutoff := s.now().Add(-s.cfg.ResultsTTL)
	removed := 0

	traceEntries, err := os.ReadDir(resolvedRoot)
	if err != nil {
		return 0, fmt.Errorf("results sweep: read workdir root: %w", err)
	}
	for _, traceEntry := range traceEntries {
		if !traceEntry.IsDir() {
			continue
		}
		traceDir := filepath.Join(resolvedRoot, traceEntry.Name())
		resultsDir := filepath.Join(traceDir, s.cfg.ResultsDirName)
		jobEntries, err := os.ReadDir(resultsDir)
		if err != nil {
			if errors.Is(err, fs.ErrNotExist) {
				continue
			}
			s.logger.Warn("results sweep: read trace results dir failed",
				"trace_dir", traceDir, "err", err.Error())
			continue
		}
		for _, jobEntry := range jobEntries {
			if !jobEntry.IsDir() {
				continue
			}
			jobDir := filepath.Join(resultsDir, jobEntry.Name())
			info, err := jobEntry.Info()
			if err != nil {
				continue
			}
			if info.ModTime().After(cutoff) {
				continue
			}
			// Defence in depth: re-canonicalise and re-check we're under root,
			// so a future symlink attack on the workdir tree cannot trick us
			// into deleting outside it.
			canon, err := filepath.EvalSymlinks(jobDir)
			if err != nil {
				continue
			}
			if !underRoot(resolvedRoot, canon) {
				s.logger.Warn("results sweep: skipping path that escapes root",
					"job_dir", jobDir, "canonical", canon)
				continue
			}
			if err := os.RemoveAll(canon); err != nil {
				s.logger.Warn("results sweep: remove failed", "job_dir", canon, "err", err.Error())
				continue
			}
			removed++
		}
	}
	return removed, nil
}

func underRoot(root, path string) bool {
	rootSep := strings.TrimRight(root, string(filepath.Separator)) + string(filepath.Separator)
	if path == root {
		return true
	}
	return strings.HasPrefix(path, rootSep)
}

func labelKeyValues(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for k, v := range m {
		out = append(out, k+"="+v)
	}
	return out
}
