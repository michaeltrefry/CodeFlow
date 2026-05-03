package workspace

import (
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func mustValidator(t *testing.T) (*Validator, string) {
	t.Helper()
	root := t.TempDir()
	v, err := New(root, ".results")
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return v, root
}

func mustMkdir(t *testing.T, parent, name string) string {
	t.Helper()
	p := filepath.Join(parent, name)
	if err := os.MkdirAll(p, 0o755); err != nil {
		t.Fatal(err)
	}
	return p
}

func TestNew_RejectsRelativeRoot(t *testing.T) {
	if _, err := New("relative/path", ""); err == nil {
		t.Fatal("expected error for relative root")
	}
}

func TestNew_RejectsNonexistentRoot(t *testing.T) {
	if _, err := New("/no/such/path/we/promise", ""); err == nil {
		t.Fatal("expected error for nonexistent root")
	}
}

func TestNew_RejectsResultsDirWithSeparator(t *testing.T) {
	root := t.TempDir()
	if _, err := New(root, "with/slash"); err == nil {
		t.Fatal("expected error for results dir with separator")
	}
}

func TestResolve_HappyPath(t *testing.T) {
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1/repo1")

	res, err := v.Resolve("trace1", "repo1", "job1")
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	expectWS := filepath.Join(root, "trace1", "repo1")
	wsCanon, _ := filepath.EvalSymlinks(expectWS)
	if res.Workspace != wsCanon {
		t.Errorf("Workspace: got %q, want %q", res.Workspace, wsCanon)
	}
	expectResults := filepath.Join(root, "trace1", ".results", "job1")
	resultsCanon, _ := filepath.EvalSymlinks(expectResults)
	if res.Results != resultsCanon {
		t.Errorf("Results: got %q, want %q", res.Results, resultsCanon)
	}
	// MkdirAll should have created the results dir.
	if info, err := os.Stat(res.Results); err != nil || !info.IsDir() {
		t.Errorf("results dir was not created: %v", err)
	}
}

func TestResolve_EmptyRepoSlug_TraceDirIsWorkspace(t *testing.T) {
	// The unified `/workspace/{traceId}` layout drops the per-repo subdir: the trace dir is
	// the workspace. Validator accepts an empty repoSlug and returns Workspace = trace dir.
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1")

	res, err := v.Resolve("trace1", "", "job1")
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	expectWS := filepath.Join(root, "trace1")
	wsCanon, _ := filepath.EvalSymlinks(expectWS)
	if res.Workspace != wsCanon {
		t.Errorf("Workspace: got %q, want %q", res.Workspace, wsCanon)
	}
	if info, err := os.Stat(res.Results); err != nil || !info.IsDir() {
		t.Errorf("results dir was not created: %v", err)
	}
}

func TestResolve_TraversalRejected(t *testing.T) {
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1/repo1")

	cases := []struct {
		name     string
		traceId  string
		repoSlug string
	}{
		{"traceId is ..", "..", "repo1"},
		{"repoSlug is ..", "trace1", ".."},
		{"traceId contains slash", "../etc", "repo1"},
		{"repoSlug contains slash", "trace1", "../../etc"},
		{"traceId is .", ".", "repo1"},
		{"repoSlug is .", "trace1", "."},
		{"traceId blank", "", "repo1"},
		// Empty repoSlug is now allowed (unified layout) — see
		// TestResolve_EmptyRepoSlug_TraceDirIsWorkspace.
		{"jobId blank", "trace1", "repo1"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			job := "job1"
			if tc.name == "jobId blank" {
				job = ""
			}
			_, err := v.Resolve(tc.traceId, tc.repoSlug, job)
			if !errors.Is(err, ErrWorkspaceInvalid) {
				t.Fatalf("expected ErrWorkspaceInvalid, got %v", err)
			}
		})
	}
}

func TestResolve_NonexistentTrace(t *testing.T) {
	v, _ := mustValidator(t)
	_, err := v.Resolve("trace-does-not-exist", "repo1", "job1")
	if !errors.Is(err, ErrWorkspaceInvalid) {
		t.Fatalf("expected ErrWorkspaceInvalid, got %v", err)
	}
}

func TestResolve_NonexistentRepo(t *testing.T) {
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1") // trace exists, repo doesn't
	_, err := v.Resolve("trace1", "repo1", "job1")
	if !errors.Is(err, ErrWorkspaceInvalid) {
		t.Fatalf("expected ErrWorkspaceInvalid, got %v", err)
	}
}

func TestResolve_RepoIsNotADirectory(t *testing.T) {
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1")
	if err := os.WriteFile(filepath.Join(root, "trace1", "repo1"), []byte("not a dir"), 0o644); err != nil {
		t.Fatal(err)
	}
	_, err := v.Resolve("trace1", "repo1", "job1")
	if !errors.Is(err, ErrWorkspaceInvalid) {
		t.Fatalf("expected ErrWorkspaceInvalid, got %v", err)
	}
}

func TestResolve_SymlinkOutOfTreeRejected(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlink semantics differ on Windows")
	}
	v, root := mustValidator(t)
	mustMkdir(t, root, "trace1")

	// Create a symlink trace1/repo1 -> /etc (out of tree).
	if err := os.Symlink("/etc", filepath.Join(root, "trace1", "repo1")); err != nil {
		t.Skipf("cannot create symlink (likely sandboxed): %v", err)
	}
	_, err := v.Resolve("trace1", "repo1", "job1")
	if !errors.Is(err, ErrWorkspaceInvalid) {
		t.Fatalf("expected ErrWorkspaceInvalid for out-of-tree symlink, got %v", err)
	}
}

func TestUnderRoot(t *testing.T) {
	cases := []struct {
		root, path string
		want       bool
	}{
		{"/a/b", "/a/b", true},
		{"/a/b", "/a/b/c", true},
		{"/a/b", "/a/bb", false},   // prefix match alone is not enough
		{"/a/b", "/a", false},
		{"/a/b/", "/a/b/c", true},
	}
	for _, tc := range cases {
		t.Run(tc.path, func(t *testing.T) {
			if got := underRoot(tc.root, tc.path); got != tc.want {
				t.Errorf("underRoot(%q,%q) = %v, want %v", tc.root, tc.path, got, tc.want)
			}
		})
	}
}
