package runner

import (
	"context"
	"encoding/binary"
	"errors"
	"io"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/apilog"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// stubDaemon is a deterministic Daemon for runner tests.
type stubDaemon struct {
	mu                   sync.Mutex
	imagePullErr         error
	imagePullCalled      bool
	createErr            error
	createName           string
	createReq            dockerd.CreateContainerRequest
	containerID          string
	startErr             error
	waitDelay            time.Duration
	waitStatus           int64
	waitErr              error
	logsBytes            []byte
	logsErr              error
	killSignal           string
	killCalled           bool
	removeForce          bool
	removeCalled         bool
}

func (s *stubDaemon) ImagePull(ctx context.Context, ref string) error {
	s.mu.Lock()
	s.imagePullCalled = true
	s.mu.Unlock()
	return s.imagePullErr
}

func (s *stubDaemon) CreateContainer(ctx context.Context, name string, req dockerd.CreateContainerRequest) (*dockerd.CreateContainerResponse, error) {
	s.mu.Lock()
	s.createName = name
	s.createReq = req
	s.mu.Unlock()
	if s.createErr != nil {
		return nil, s.createErr
	}
	id := s.containerID
	if id == "" {
		id = "abc123"
	}
	return &dockerd.CreateContainerResponse{ID: id}, nil
}

func (s *stubDaemon) StartContainer(ctx context.Context, id string) error {
	return s.startErr
}

func (s *stubDaemon) WaitContainer(ctx context.Context, id string) (*dockerd.WaitContainerResponse, error) {
	if s.waitDelay > 0 {
		select {
		case <-time.After(s.waitDelay):
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	if s.waitErr != nil {
		return nil, s.waitErr
	}
	return &dockerd.WaitContainerResponse{StatusCode: s.waitStatus}, nil
}

func (s *stubDaemon) ContainerLogs(ctx context.Context, id string) (closeReader, error) {
	if s.logsErr != nil {
		return nil, s.logsErr
	}
	return io.NopCloser(strings.NewReader(string(s.logsBytes))), nil
}

func (s *stubDaemon) KillContainer(ctx context.Context, id, signal string) error {
	s.mu.Lock()
	s.killCalled = true
	s.killSignal = signal
	s.mu.Unlock()
	return nil
}

func (s *stubDaemon) RemoveContainer(ctx context.Context, id string, force bool) error {
	s.mu.Lock()
	s.removeCalled = true
	s.removeForce = force
	s.mu.Unlock()
	return nil
}

// dockerFrame builds one multiplexed-log frame.
func dockerFrame(stream byte, payload string) []byte {
	hdr := make([]byte, 8)
	hdr[0] = stream
	binary.BigEndian.PutUint32(hdr[4:], uint32(len(payload)))
	return append(hdr, []byte(payload)...)
}

func newRunner(d Daemon) *Runner {
	return New(d, apilog.New(slog.LevelError), 5*time.Second, 5*time.Second)
}

func goodSpec() JobSpec {
	return JobSpec{
		JobID:          "01951f8d-0123-7abc-89ab-cdef00112233",
		TraceID:        "abc",
		RepoSlug:       "r",
		Image:          "alpine:3",
		Cmd:            []string{"echo", "hi"},
		CPUs:           1.0,
		MemoryBytes:    1 << 30,
		PidsLimit:      256,
		TimeoutSeconds: 60,
		StdoutMaxBytes: 1 << 20,
		StderrMaxBytes: 1 << 20,
	}
}

func TestRunner_HappyPath(t *testing.T) {
	stub := &stubDaemon{
		containerID: "deadbeef",
		waitStatus:  0,
		logsBytes: append(append([]byte{}, dockerFrame(1, "hi\n")...),
			dockerFrame(2, "warning\n")...),
	}
	r := newRunner(stub)
	res, err := r.Run(context.Background(), goodSpec())
	if err != nil {
		t.Fatal(err)
	}
	if res.ExitCode != 0 {
		t.Fatalf("exit: %d", res.ExitCode)
	}
	if res.Stdout != "hi\n" {
		t.Fatalf("stdout: %q", res.Stdout)
	}
	if res.Stderr != "warning\n" {
		t.Fatalf("stderr: %q", res.Stderr)
	}
	if res.TimedOut || res.Cancelled {
		t.Fatalf("flags wrong: timedOut=%v cancelled=%v", res.TimedOut, res.Cancelled)
	}
	if !stub.imagePullCalled {
		t.Error("ImagePull not called")
	}
	if !stub.removeCalled || !stub.removeForce {
		t.Error("RemoveContainer with force not called")
	}
	if !strings.HasPrefix(stub.createName, "cf-job-") {
		t.Errorf("container name: %q", stub.createName)
	}
}

func TestRunner_PropagatesNonZeroExit(t *testing.T) {
	stub := &stubDaemon{waitStatus: 7, logsBytes: dockerFrame(1, "fail\n")}
	r := newRunner(stub)
	res, err := r.Run(context.Background(), goodSpec())
	if err != nil {
		t.Fatal(err)
	}
	if res.ExitCode != 7 {
		t.Fatalf("exit: %d", res.ExitCode)
	}
	if res.Stdout != "fail\n" {
		t.Fatalf("stdout: %q", res.Stdout)
	}
}

func TestRunner_TimeoutKillsContainer(t *testing.T) {
	stub := &stubDaemon{
		// Wait blocks past the per-job timeout.
		waitDelay:  500 * time.Millisecond,
		waitStatus: 0,
		// No logs frames available — empty stream is fine.
	}
	spec := goodSpec()
	spec.TimeoutSeconds = 1 // 1s, but waitDelay is 500ms — wait completes first

	// Make wait actually exceed the spec timeout.
	stub.waitDelay = 2 * time.Second
	r := newRunner(stub)
	res, err := r.Run(context.Background(), spec)
	if err != nil {
		t.Fatal(err)
	}
	if !res.TimedOut {
		t.Fatal("expected TimedOut")
	}
	if !stub.killCalled || stub.killSignal != "SIGKILL" {
		t.Errorf("kill expected with SIGKILL, got called=%v signal=%q", stub.killCalled, stub.killSignal)
	}
	if !stub.removeCalled {
		t.Error("RemoveContainer not called on timeout")
	}
}

func TestRunner_CancellationKillsContainer(t *testing.T) {
	stub := &stubDaemon{waitDelay: 5 * time.Second, waitStatus: 0}
	ctx, cancel := context.WithCancel(context.Background())
	r := newRunner(stub)

	resCh := make(chan *Result, 1)
	errCh := make(chan error, 1)
	go func() {
		res, err := r.Run(ctx, goodSpec())
		resCh <- res
		errCh <- err
	}()

	time.Sleep(100 * time.Millisecond)
	cancel()

	select {
	case res := <-resCh:
		<-errCh // discard
		if !res.Cancelled {
			t.Fatalf("expected Cancelled, got %#v", res)
		}
		if !stub.killCalled {
			t.Error("kill not called")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("runner did not return after cancel")
	}
}

func TestRunner_ImagePullErrorPropagates(t *testing.T) {
	stub := &stubDaemon{imagePullErr: errors.New("registry unreachable")}
	r := newRunner(stub)
	_, err := r.Run(context.Background(), goodSpec())
	if err == nil || !strings.Contains(err.Error(), "image pull") {
		t.Fatalf("expected image pull error, got %v", err)
	}
}

func TestRunner_StreamCapsApply(t *testing.T) {
	// 100 KB of stdout, 50 KB of stderr; cap each at 10 KB.
	bigStdout := strings.Repeat("a", 100*1024)
	bigStderr := strings.Repeat("b", 50*1024)
	stub := &stubDaemon{
		waitStatus: 0,
		logsBytes:  append(dockerFrame(1, bigStdout), dockerFrame(2, bigStderr)...),
	}
	spec := goodSpec()
	spec.StdoutMaxBytes = 10 * 1024
	spec.StderrMaxBytes = 10 * 1024
	r := newRunner(stub)
	res, err := r.Run(context.Background(), spec)
	if err != nil {
		t.Fatal(err)
	}
	if int64(len(res.Stdout)) != spec.StdoutMaxBytes {
		t.Errorf("stdout cap not enforced: len=%d want=%d", len(res.Stdout), spec.StdoutMaxBytes)
	}
	if !res.StdoutTruncated {
		t.Error("StdoutTruncated must be true")
	}
	if int64(len(res.Stderr)) != spec.StderrMaxBytes {
		t.Errorf("stderr cap: len=%d", len(res.Stderr))
	}
	if !res.StderrTruncated {
		t.Error("StderrTruncated must be true")
	}
}
