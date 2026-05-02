package sweeper

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/apilog"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// stubDaemon is a deterministic Daemon for sweeper tests.
type stubDaemon struct {
	mu             sync.Mutex
	listErr        error
	listFilter     string
	listAll        bool
	items          []dockerd.ContainerListItem
	removed        []string
	removeErrFor   map[string]error
}

func (s *stubDaemon) ListContainers(ctx context.Context, filter string, all bool) ([]dockerd.ContainerListItem, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.listFilter = filter
	s.listAll = all
	if s.listErr != nil {
		return nil, s.listErr
	}
	return append([]dockerd.ContainerListItem(nil), s.items...), nil
}

func (s *stubDaemon) RemoveContainer(ctx context.Context, id string, force bool) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if e, ok := s.removeErrFor[id]; ok {
		return e
	}
	s.removed = append(s.removed, id)
	return nil
}

func newSweeper(t *testing.T, daemon Daemon, cfg Config) *Sweeper {
	t.Helper()
	s := New(daemon, cfg, apilog.New(slog.LevelError))
	s.now = func() time.Time { return time.Date(2026, 5, 2, 12, 0, 0, 0, time.UTC) }
	return s
}

func TestSweepContainers_RemovesOnlyOlderThanTTL(t *testing.T) {
	stub := &stubDaemon{
		items: []dockerd.ContainerListItem{
			{ID: "old-1", Created: time.Date(2026, 5, 1, 0, 0, 0, 0, time.UTC).Unix()},   // 36h old
			{ID: "fresh-1", Created: time.Date(2026, 5, 2, 6, 0, 0, 0, time.UTC).Unix()}, // 6h old
			{ID: "old-2", Created: time.Date(2026, 4, 30, 0, 0, 0, 0, time.UTC).Unix()},  // 60h old
		},
	}
	s := newSweeper(t, stub, Config{ContainerTTL: 24 * time.Hour})
	res := s.RunOnce(context.Background())

	if res.ContainersRemoved != 2 {
		t.Fatalf("ContainersRemoved: %d", res.ContainersRemoved)
	}
	if res.Errors != 0 {
		t.Fatalf("Errors: %d", res.Errors)
	}
	if stub.listFilter != "cf-managed=true" {
		t.Errorf("expected list filter cf-managed=true, got %q", stub.listFilter)
	}
	if !stub.listAll {
		t.Error("expected all=true on list call")
	}
	removed := map[string]bool{}
	for _, id := range stub.removed {
		removed[id] = true
	}
	if !removed["old-1"] || !removed["old-2"] {
		t.Errorf("expected old-1 + old-2 removed, got %v", stub.removed)
	}
	if removed["fresh-1"] {
		t.Errorf("must not remove fresh-1, got %v", stub.removed)
	}
}

func TestSweepContainers_PausesOnDaemonError(t *testing.T) {
	stub := &stubDaemon{listErr: errors.New("connection refused")}
	s := newSweeper(t, stub, Config{ContainerTTL: time.Hour})
	res := s.RunOnce(context.Background())

	if res.Errors == 0 {
		t.Fatal("expected an error count")
	}
	if res.ContainersRemoved != 0 {
		t.Fatal("must not remove anything when list fails")
	}
	if !s.daemonUnhealthy {
		t.Error("daemonUnhealthy must be set after a list failure")
	}

	// Recover the daemon — health flips back.
	stub.listErr = nil
	stub.items = nil
	res = s.RunOnce(context.Background())
	if res.Errors != 0 {
		t.Fatalf("Errors after recovery: %d", res.Errors)
	}
	if s.daemonUnhealthy {
		t.Error("daemonUnhealthy must clear after a successful list")
	}
}

func TestSweepResultsDirs_RemovesOlderThanTTL(t *testing.T) {
	root := t.TempDir()
	mkdir := func(rel string) string {
		p := filepath.Join(root, rel)
		if err := os.MkdirAll(p, 0o755); err != nil {
			t.Fatal(err)
		}
		return p
	}

	now := time.Date(2026, 5, 2, 12, 0, 0, 0, time.UTC)
	freshDir := mkdir("trace1/.results/job-fresh")
	oldDir := mkdir("trace1/.results/job-old")
	otherTraceOld := mkdir("trace2/.results/job-other-old")
	mkdir("trace2/repo")
	mkdir("trace2/.results/file-not-dir-noop") // covered below

	// Set mtimes.
	if err := os.Chtimes(freshDir, now.Add(-time.Hour), now.Add(-time.Hour)); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(oldDir, now.Add(-30*24*time.Hour), now.Add(-30*24*time.Hour)); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(otherTraceOld, now.Add(-15*24*time.Hour), now.Add(-15*24*time.Hour)); err != nil {
		t.Fatal(err)
	}

	stub := &stubDaemon{}
	s := newSweeper(t, stub, Config{
		WorkdirRoot:    root,
		ResultsDirName: ".results",
		ResultsTTL:     7 * 24 * time.Hour,
	})
	s.now = func() time.Time { return now }

	res := s.RunOnce(context.Background())

	if res.ResultsDirsRemoved != 2 {
		t.Errorf("expected 2 dirs removed, got %d", res.ResultsDirsRemoved)
	}
	if _, err := os.Stat(freshDir); err != nil {
		t.Error("fresh dir must remain")
	}
	if _, err := os.Stat(oldDir); err == nil {
		t.Error("old dir must be removed")
	}
	if _, err := os.Stat(otherTraceOld); err == nil {
		t.Error("old dir under other trace must be removed")
	}
}

func TestSweepResultsDirs_NoOpWhenWorkdirRootEmpty(t *testing.T) {
	stub := &stubDaemon{}
	s := newSweeper(t, stub, Config{}) // WorkdirRoot empty
	res := s.RunOnce(context.Background())
	if res.ResultsDirsRemoved != 0 {
		t.Fatal("must not walk anything without a configured root")
	}
}

func TestSweepContainers_HandlesRemoveError(t *testing.T) {
	stub := &stubDaemon{
		items: []dockerd.ContainerListItem{
			{ID: "a", Created: time.Date(2026, 5, 1, 0, 0, 0, 0, time.UTC).Unix()},
			{ID: "b", Created: time.Date(2026, 5, 1, 0, 0, 0, 0, time.UTC).Unix()},
		},
		removeErrFor: map[string]error{
			"a": errors.New("docker rm: 500: daemon error"),
		},
	}
	s := newSweeper(t, stub, Config{ContainerTTL: 24 * time.Hour})
	res := s.RunOnce(context.Background())
	if res.ContainersRemoved != 1 {
		t.Errorf("expected 1 success despite the error on a, got %d", res.ContainersRemoved)
	}
}

func TestNew_AppliesDefaults(t *testing.T) {
	s := New(&stubDaemon{}, Config{}, nil)
	if s.cfg.Interval != DefaultInterval {
		t.Errorf("Interval default not applied: %v", s.cfg.Interval)
	}
	if s.cfg.ContainerTTL != DefaultContainerTTL {
		t.Errorf("ContainerTTL default not applied: %v", s.cfg.ContainerTTL)
	}
	if s.cfg.ResultsTTL != DefaultResultsTTL {
		t.Errorf("ResultsTTL default not applied: %v", s.cfg.ResultsTTL)
	}
	if s.cfg.ResultsDirName != ".results" {
		t.Errorf("ResultsDirName default not applied: %q", s.cfg.ResultsDirName)
	}
}
