package config

import (
	"path/filepath"
	"testing"
)

// TestCanonicalConfigLoads guards the in-repo controller-config.toml that the
// deploy workflow scps verbatim to the host. A bad config exit-2's the
// controller on startup, so we catch parse / validate failures in CI before the
// file goes out.
//
// Path: this test sits at sandbox-controller/internal/config/, the file at
// sandbox-controller/deploy/controller-config.toml, so ../../deploy/... is the
// canonical relative path.
func TestCanonicalConfigLoads(t *testing.T) {
	path := filepath.Join("..", "..", "deploy", "controller-config.toml")
	cfg, err := Load(path)
	if err != nil {
		t.Fatalf("canonical controller-config.toml failed to load: %v", err)
	}

	// Spot-check the bits the deploy fix is anchoring against — a future edit
	// that regresses these is what landed us in the manual-edit-on-host loop.
	if cfg.Workspace.WorkdirRoot != "/workspace" {
		t.Errorf("workspace.workdir_root = %q, want %q (must match the api/worker WorkspaceOptions.WorkingDirectoryRoot)", cfg.Workspace.WorkdirRoot, "/workspace")
	}
	if len(cfg.Images.Allowed) == 0 {
		t.Error("images.allowed is empty — every /run would be rejected with image_not_allowed")
	}
	if len(cfg.TLS.AllowedClientSubjects) == 0 {
		t.Error("tls.allowed_client_subjects is empty — mTLS handshake would reject every caller")
	}
}
