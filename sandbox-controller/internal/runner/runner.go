// Package runner orchestrates a single job's lifecycle: pull the image, create
// the container with the controller's locked-down defaults, start it, wait for
// exit (with the per-job timeout), capture stdout/stderr (bounded by per-job
// caps), tear it down. The runner does NOT decide policy — the layer-2
// validator (sc-530, sc-531) gates which JobSpecs reach this code.
package runner

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// Result is the outcome of a Run call. Fields mirror the controller's
// /run response shape (see docs/sandbox-executor.md §11.3).
type Result struct {
	JobID           string
	ExitCode        int
	Stdout          string
	Stderr          string
	StdoutTruncated bool
	StderrTruncated bool
	TimedOut        bool
	Cancelled       bool
	DurationMs      int64
}

// Daemon is the subset of dockerd.Client the runner uses. Defined here as an
// interface so tests can stub it without standing up an httptest daemon.
type Daemon interface {
	ImagePull(ctx context.Context, ref string) error
	CreateContainer(ctx context.Context, name string, req dockerd.CreateContainerRequest) (*dockerd.CreateContainerResponse, error)
	StartContainer(ctx context.Context, id string) error
	WaitContainer(ctx context.Context, id string) (*dockerd.WaitContainerResponse, error)
	ContainerLogs(ctx context.Context, id string) (closeReader, error)
	KillContainer(ctx context.Context, id, signal string) error
	RemoveContainer(ctx context.Context, id string, force bool) error
}

// closeReader is io.ReadCloser; named locally so the Daemon interface stays
// importable without depending on io.
type closeReader interface {
	Read(p []byte) (int, error)
	Close() error
}

// Runner orchestrates a single Run call. Construct with New.
type Runner struct {
	daemon            Daemon
	logger            *slog.Logger
	imagePullTimeout  time.Duration
	containerTearTimeout time.Duration
}

// New returns a Runner. imagePullTimeout caps how long the daemon is allowed
// to pull an image before the runner aborts the call (separate budget from the
// per-job timeout). containerTearTimeout caps how long teardown can take.
func New(daemon Daemon, logger *slog.Logger, imagePullTimeout, containerTearTimeout time.Duration) *Runner {
	if logger == nil {
		logger = slog.Default()
	}
	if imagePullTimeout <= 0 {
		imagePullTimeout = 10 * time.Minute
	}
	if containerTearTimeout <= 0 {
		containerTearTimeout = 30 * time.Second
	}
	return &Runner{
		daemon:            daemon,
		logger:            logger,
		imagePullTimeout:  imagePullTimeout,
		containerTearTimeout: containerTearTimeout,
	}
}

// Run executes the spec against the daemon. The returned Result is populated
// even on failure paths where partial output is available (e.g. timeout).
//
// ctx cancellation is propagated as a deliberate "cancel this job"; the runner
// kills the running container, drains its logs to whatever was captured so
// far, and returns Cancelled=true.
func (r *Runner) Run(ctx context.Context, spec JobSpec) (*Result, error) {
	if err := spec.Validate(); err != nil {
		return nil, fmt.Errorf("invalid spec: %w", err)
	}

	start := time.Now()
	res := &Result{JobID: spec.JobID}
	defer func() {
		res.DurationMs = time.Since(start).Milliseconds()
	}()

	// 1) Pull the image (separate timeout budget so a slow registry doesn't
	// eat into the per-job timeout).
	pullCtx, pullCancel := context.WithTimeout(ctx, r.imagePullTimeout)
	if err := r.daemon.ImagePull(pullCtx, spec.Image); err != nil {
		pullCancel()
		return res, fmt.Errorf("image pull: %w", err)
	}
	pullCancel()

	// 2) Create the container with the locked-down defaults.
	createReq := spec.ToCreateRequest()
	create, err := r.daemon.CreateContainer(ctx, spec.ContainerName(), createReq)
	if err != nil {
		return res, fmt.Errorf("container create: %w", err)
	}
	containerID := create.ID
	r.logger.Info("container created",
		"job_id", spec.JobID,
		"container_id", containerID,
		"image", spec.Image,
		"runtime", createReq.Runtime,
	)

	// Always tear down before returning, regardless of how we got here.
	defer r.teardown(containerID, spec.JobID)

	// 3) Start.
	if err := r.daemon.StartContainer(ctx, containerID); err != nil {
		return res, fmt.Errorf("container start: %w", err)
	}

	// 4) Wait, with the per-job timeout.
	waitCtx, waitCancel := context.WithTimeout(ctx, spec.Timeout())
	wait, waitErr := r.daemon.WaitContainer(waitCtx, containerID)
	waitCancel()

	switch {
	case waitErr == nil:
		res.ExitCode = int(wait.StatusCode)
	case errors.Is(waitErr, context.DeadlineExceeded):
		res.TimedOut = true
		// Kill the container so it doesn't keep running while we tear down.
		killCtx, killCancel := context.WithTimeout(context.Background(), r.containerTearTimeout)
		if err := r.daemon.KillContainer(killCtx, containerID, "SIGKILL"); err != nil && !dockerd.IsNotFound(err) {
			r.logger.Warn("kill on timeout failed", "job_id", spec.JobID, "err", err.Error())
		}
		killCancel()
	case errors.Is(waitErr, context.Canceled) || errors.Is(ctx.Err(), context.Canceled):
		res.Cancelled = true
		killCtx, killCancel := context.WithTimeout(context.Background(), r.containerTearTimeout)
		if err := r.daemon.KillContainer(killCtx, containerID, "SIGKILL"); err != nil && !dockerd.IsNotFound(err) {
			r.logger.Warn("kill on cancel failed", "job_id", spec.JobID, "err", err.Error())
		}
		killCancel()
	default:
		return res, fmt.Errorf("container wait: %w", waitErr)
	}

	// 5) Capture logs. Use a fresh context — even on timeout/cancel we still
	// want to surface whatever output the container managed to write.
	logsCtx, logsCancel := context.WithTimeout(context.Background(), r.containerTearTimeout)
	defer logsCancel()
	stream, err := r.daemon.ContainerLogs(logsCtx, containerID)
	if err != nil {
		return res, fmt.Errorf("container logs: %w", err)
	}
	defer stream.Close()

	stdoutBuf := NewBoundedBuffer(spec.StdoutMaxBytes)
	stderrBuf := NewBoundedBuffer(spec.StderrMaxBytes)
	if err := dockerd.Demux(stream, stdoutBuf, stderrBuf); err != nil {
		// Demux errors are usually transport blips after the container is
		// already gone; log and surface the partial output.
		r.logger.Warn("log demux", "job_id", spec.JobID, "err", err.Error())
	}

	res.Stdout = stdoutBuf.String()
	res.Stderr = stderrBuf.String()
	res.StdoutTruncated = stdoutBuf.Truncated()
	res.StderrTruncated = stderrBuf.Truncated()
	return res, nil
}

// teardown removes the container. Best-effort — used in defer.
func (r *Runner) teardown(containerID, jobID string) {
	ctx, cancel := context.WithTimeout(context.Background(), r.containerTearTimeout)
	defer cancel()
	if err := r.daemon.RemoveContainer(ctx, containerID, true); err != nil && !dockerd.IsNotFound(err) {
		r.logger.Warn("container remove failed",
			"job_id", jobID,
			"container_id", containerID,
			"err", err.Error(),
		)
	}
}

// realDaemon adapts a *dockerd.Client to the Daemon interface. The Daemon
// interface declares ContainerLogs returning closeReader; dockerd.Client
// returns io.ReadCloser which already satisfies that.
type realDaemon struct{ c *dockerd.Client }

// NewRealDaemon wraps a dockerd.Client.
func NewRealDaemon(c *dockerd.Client) Daemon { return &realDaemon{c: c} }

func (d *realDaemon) ImagePull(ctx context.Context, ref string) error {
	return d.c.ImagePull(ctx, ref)
}
func (d *realDaemon) CreateContainer(ctx context.Context, name string, req dockerd.CreateContainerRequest) (*dockerd.CreateContainerResponse, error) {
	return d.c.CreateContainer(ctx, name, req)
}
func (d *realDaemon) StartContainer(ctx context.Context, id string) error {
	return d.c.StartContainer(ctx, id)
}
func (d *realDaemon) WaitContainer(ctx context.Context, id string) (*dockerd.WaitContainerResponse, error) {
	return d.c.WaitContainer(ctx, id)
}
func (d *realDaemon) ContainerLogs(ctx context.Context, id string) (closeReader, error) {
	rc, err := d.c.ContainerLogs(ctx, id)
	if err != nil {
		return nil, err
	}
	return rc, nil
}
func (d *realDaemon) KillContainer(ctx context.Context, id, signal string) error {
	return d.c.KillContainer(ctx, id, signal)
}
func (d *realDaemon) RemoveContainer(ctx context.Context, id string, force bool) error {
	return d.c.RemoveContainer(ctx, id, force)
}
