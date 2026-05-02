//go:build integration

package runner

import (
	"context"
	"log/slog"
	"strings"
	"testing"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/apilog"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// skipUnlessExecutorHost short-circuits these tests when the host doesn't have
// what they need: a reachable docker daemon AND the `runsc` runtime registered.
// Local dev (Docker Desktop on macOS) usually has the daemon but not runsc; CI
// on the executor VM has both.
func skipUnlessExecutorHost(t *testing.T) *dockerd.Client {
	t.Helper()
	d := dockerd.New("")
	pingCtx, pingCancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer pingCancel()
	if err := d.Ping(pingCtx); err != nil {
		t.Skipf("docker daemon unreachable: %v", err)
	}
	// Try to create-and-immediately-remove a stub container that asks for
	// runsc; if the daemon rejects with "unknown or invalid runtime name",
	// runsc isn't registered and we skip.
	probeCtx, probeCancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer probeCancel()
	resp, err := d.CreateContainer(probeCtx, "", dockerd.CreateContainerRequest{
		Image:       "alpine:3",
		Cmd:         []string{"true"},
		Runtime:     "runsc",
		NetworkMode: "none",
	})
	if err != nil {
		if strings.Contains(err.Error(), "unknown or invalid runtime") {
			t.Skipf("gVisor (runsc) runtime not registered with dockerd: %v", err)
		}
		// Probably an image-not-found — let the actual test surface that.
	}
	if resp != nil {
		// Best-effort cleanup of the probe container.
		_ = d.RemoveContainer(context.Background(), resp.ID, true)
	}
	return d
}

// TestRunner_AlpineEcho is the sc-529 acceptance integration test from the
// slice description: receive a request to run alpine:3 with cmd=["echo","hi"],
// return exit 0 with stdout "hi\n", and the container is gone after the
// response.
//
// Build with:  go test -tags=integration ./internal/runner/...
//
// Requirements on the host:
//   - dockerd running and accessible at /var/run/docker.sock
//   - runsc installed (`/etc/docker/daemon.json` registers it)
//   - Internet for the alpine image pull (or the image already cached)
//
// Skipped if docker is not reachable (allows local dev where the binary
// builds but the daemon isn't available).
func TestRunner_AlpineEcho(t *testing.T) {
	d := skipUnlessExecutorHost(t)

	r := New(NewRealDaemon(d), apilog.New(slog.LevelInfo), 2*time.Minute, 30*time.Second)
	spec := JobSpec{
		JobID:          "01951f8d-0123-7abc-89ab-cdef00112233",
		TraceID:        "integration-test",
		RepoSlug:       "n/a",
		Image:          "alpine:3",
		Cmd:            []string{"echo", "hi"},
		CPUs:           1.0,
		MemoryBytes:    256 * 1024 * 1024,
		PidsLimit:      64,
		TimeoutSeconds: 30,
		StdoutMaxBytes: 1 << 20,
		StderrMaxBytes: 1 << 20,
	}

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	res, err := r.Run(ctx, spec)
	if err != nil {
		t.Fatalf("Run failed: %v", err)
	}
	if res.ExitCode != 0 {
		t.Errorf("exit code: %d", res.ExitCode)
	}
	if res.Stdout != "hi\n" {
		t.Errorf("stdout: %q (want %q)", res.Stdout, "hi\n")
	}
	if res.TimedOut || res.Cancelled {
		t.Errorf("unexpected flags: timedOut=%v cancelled=%v", res.TimedOut, res.Cancelled)
	}
	t.Logf("alpine echo hi: exit=%d stdout=%q duration=%dms", res.ExitCode, strings.TrimSpace(res.Stdout), res.DurationMs)
}

// TestRunner_TimeoutOnRealDaemon exercises the SIGKILL-on-timeout path.
func TestRunner_TimeoutOnRealDaemon(t *testing.T) {
	d := skipUnlessExecutorHost(t)

	r := New(NewRealDaemon(d), apilog.New(slog.LevelInfo), 2*time.Minute, 30*time.Second)
	spec := JobSpec{
		JobID:          "01951f8d-0123-7abc-89ab-cdef00112299",
		TraceID:        "integration-timeout",
		RepoSlug:       "n/a",
		Image:          "alpine:3",
		Cmd:            []string{"sleep", "60"},
		CPUs:           1.0,
		MemoryBytes:    256 * 1024 * 1024,
		PidsLimit:      64,
		TimeoutSeconds: 2, // sleep 60 must not finish in 2s
		StdoutMaxBytes: 1 << 20,
		StderrMaxBytes: 1 << 20,
	}

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	res, err := r.Run(ctx, spec)
	if err != nil {
		t.Fatalf("Run failed: %v", err)
	}
	if !res.TimedOut {
		t.Error("expected TimedOut=true")
	}
	t.Logf("timeout path: timedOut=%v duration=%dms", res.TimedOut, res.DurationMs)
}
