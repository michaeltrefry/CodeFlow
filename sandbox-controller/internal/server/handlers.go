package server

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"

	"github.com/google/uuid"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/runner"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/whitelist"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/workspace"
)

// hashImage returns a short stable digest for log correlation. We deliberately
// don't put the raw image string into operator logs on reject paths — the
// rejected name is a partial leak of intent.
func hashImage(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:6])
}

// RunRequest matches the schema in docs/sandbox-executor.md §11.2. Decoded
// strictly: unknown fields are rejected to defend against schema-bypass /
// type-confusion attacks (see §10.2 "no unknown fields" rule).
type RunRequest struct {
	JobID    string            `json:"jobId"`
	TraceID  string            `json:"traceId"`
	RepoSlug string            `json:"repoSlug"`
	Image    string            `json:"image"`
	Cmd      []string          `json:"cmd"`
	Env      map[string]string `json:"env,omitempty"`
	Limits   Limits            `json:"limits"`
}

// Limits is the per-job resource cap section. All values are required at the
// schema level; the controller-side validator (sc-530) enforces upper bounds.
type Limits struct {
	Cpus           float64 `json:"cpus"`
	MemoryBytes    int64   `json:"memoryBytes"`
	Pids           int     `json:"pids"`
	TimeoutSeconds int     `json:"timeoutSeconds"`
	StdoutMaxBytes int64   `json:"stdoutMaxBytes"`
	StderrMaxBytes int64   `json:"stderrMaxBytes"`
}

// RunResponse mirrors docs/sandbox-executor.md §11.3.
type RunResponse struct {
	JobID           string          `json:"jobId"`
	ExitCode        int             `json:"exitCode"`
	Stdout          string          `json:"stdout"`
	Stderr          string          `json:"stderr"`
	StdoutTruncated bool            `json:"stdoutTruncated"`
	StderrTruncated bool            `json:"stderrTruncated"`
	TimedOut        bool            `json:"timedOut"`
	Cancelled       bool            `json:"cancelled"`
	DurationMs      int64           `json:"durationMs"`
	Artifacts       []ArtifactEntry `json:"artifacts"`
}

// ArtifactEntry is one file in the per-job /artifacts directory. sc-531
// populates this from runner.Result.Artifacts; api/worker resolves the host
// path on its side via the same workspace mount.
type ArtifactEntry struct {
	Path      string `json:"path"`
	SizeBytes int64  `json:"sizeBytes"`
	Kind      string `json:"kind"`
}

// CancelRequest is the cancel-by-jobId payload.
type CancelRequest struct {
	JobID string `json:"jobId"`
}

// errorEnvelope matches docs/sandbox-executor.md §11.4.
type errorEnvelope struct {
	Error errorBody `json:"error"`
}
type errorBody struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Rule    string `json:"rule,omitempty"`
	JobID   string `json:"jobId,omitempty"`
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if body != nil {
		_ = json.NewEncoder(w).Encode(body)
	}
}

func writeError(w http.ResponseWriter, status int, code, msg, rule, jobID string) {
	writeJSON(w, status, errorEnvelope{Error: errorBody{
		Code:    code,
		Message: msg,
		Rule:    rule,
		JobID:   jobID,
	}})
}

// strictDecode JSON-decodes into v with DisallowUnknownFields.
func strictDecode(r *http.Request, v any) error {
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(v); err != nil {
		return err
	}
	// Reject trailing tokens too — protects against JSON smuggling in chunked bodies.
	if dec.More() {
		return errors.New("unexpected trailing JSON in request body")
	}
	return nil
}

func (s *Server) handleRun(w http.ResponseWriter, r *http.Request) {
	var req RunRequest
	if err := strictDecode(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, "request_invalid", err.Error(), "decoder", "")
		return
	}

	// Layer-2 entry: scaffold-shape validation. sc-530 / sc-531 add the image
	// whitelist and workspace-path checks. The strict-decode above already
	// rejected unknown fields (defends against attempts to set privileged,
	// host network, etc. — those keys are not on RunRequest so they 400).
	if err := validateScaffoldShape(&req); err != nil {
		writeError(w, http.StatusBadRequest, "request_invalid", err.Error(), "scaffold_shape", req.JobID)
		return
	}

	if s.jobRunner == nil {
		writeError(w, http.StatusServiceUnavailable, "controller_unavailable",
			"job runner not configured", "runner.nil", req.JobID)
		return
	}

	// Layer-2 image-allowlist check (sc-530). Independent of CodeFlow's
	// ContainerToolOptions. Default-deny — an empty (or nil) allowlist
	// rejects every image. The error is logged at info with a hash of the
	// offending image rather than the raw value, per
	// docs/sandbox-executor.md §10.4 (don't leak rejected-request intent
	// into operator logs).
	if al := s.allowlist(); al != nil {
		if err := al.Verify(req.Image); err != nil {
			s.logger.Info("rejected: image not on allowlist",
				"job_id", req.JobID,
				"trace_id", req.TraceID,
				"image_hash", hashImage(req.Image),
			)
			writeError(w, http.StatusForbidden, "image_not_allowed",
				whitelist.ErrImageNotAllowed.Error(), "validator.imageWhitelist", req.JobID)
			return
		}
	}

	spec := req.toJobSpec()

	// Layer-2 workspace-path resolution + validation (sc-531). When the
	// controller is configured without a validator (test path), spec is
	// passed through with empty workspace paths and the runner skips the
	// bind-mount. In production main.go always passes a validator.
	if s.wsValidator != nil {
		resolved, err := s.wsValidator.Resolve(req.TraceID, req.RepoSlug, req.JobID)
		if err != nil {
			s.logger.Info("rejected: workspace invalid",
				"job_id", req.JobID,
				"trace_id", req.TraceID,
				"err", err.Error(),
			)
			code := "workspace_invalid"
			msg := workspace.ErrWorkspaceInvalid.Error()
			if !errors.Is(err, workspace.ErrWorkspaceInvalid) {
				// Programmer error — surface internally.
				code = "internal_error"
				msg = err.Error()
			}
			writeError(w, http.StatusBadRequest, code, msg, "validator.workspacePath", req.JobID)
			return
		}
		spec.WorkspaceHostPath = resolved.Workspace
		spec.ResultsHostPath = resolved.Results
	}

	res, err := s.jobRunner.Run(r.Context(), spec)
	if err != nil {
		s.logger.Warn("job run failed",
			"job_id", req.JobID,
			"trace_id", req.TraceID,
			"err", err.Error(),
		)
		// Surface the partial Result if the runner returned one (timeout, etc.).
		jobID := req.JobID
		if res != nil {
			writeJSON(w, http.StatusInternalServerError, errorEnvelope{Error: errorBody{
				Code:    "daemon_error",
				Message: err.Error(),
				Rule:    "runner.error",
				JobID:   jobID,
			}})
			return
		}
		writeError(w, http.StatusInternalServerError, "daemon_error", err.Error(), "runner.error", jobID)
		return
	}

	writeJSON(w, http.StatusOK, RunResponse{
		JobID:           res.JobID,
		ExitCode:        res.ExitCode,
		Stdout:          res.Stdout,
		Stderr:          res.Stderr,
		StdoutTruncated: res.StdoutTruncated,
		StderrTruncated: res.StderrTruncated,
		TimedOut:        res.TimedOut,
		Cancelled:       res.Cancelled,
		DurationMs:      res.DurationMs,
		Artifacts:       toResponseArtifacts(res.Artifacts),
	})
}

// toResponseArtifacts maps the runner's artifact list to the wire-format
// shape (capitalised field names → camelCase JSON).
func toResponseArtifacts(in []runner.Artifact) []ArtifactEntry {
	out := make([]ArtifactEntry, 0, len(in))
	for _, a := range in {
		out = append(out, ArtifactEntry{
			Path:      a.Path,
			SizeBytes: a.SizeBytes,
			Kind:      a.Kind,
		})
	}
	return out
}

// toJobSpec converts the wire-format RunRequest into the runner's internal
// JobSpec. Fields that the wire format doesn't carry (workspace path, etc.)
// are populated by later slices.
func (req RunRequest) toJobSpec() runner.JobSpec {
	return runner.JobSpec{
		JobID:          req.JobID,
		TraceID:        req.TraceID,
		RepoSlug:       req.RepoSlug,
		Image:          req.Image,
		Cmd:            req.Cmd,
		Env:            req.Env,
		CPUs:           req.Limits.Cpus,
		MemoryBytes:    req.Limits.MemoryBytes,
		PidsLimit:      int64(req.Limits.Pids),
		TimeoutSeconds: req.Limits.TimeoutSeconds,
		StdoutMaxBytes: req.Limits.StdoutMaxBytes,
		StderrMaxBytes: req.Limits.StderrMaxBytes,
	}
}

func (s *Server) handleCancel(w http.ResponseWriter, r *http.Request) {
	var req CancelRequest
	if err := strictDecode(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, "request_invalid", err.Error(), "decoder", "")
		return
	}
	if strings.TrimSpace(req.JobID) == "" {
		writeError(w, http.StatusBadRequest, "request_invalid", "jobId must be set", "scaffold_shape", "")
		return
	}
	if _, err := uuid.Parse(req.JobID); err != nil {
		writeError(w, http.StatusBadRequest, "request_invalid", "jobId must be a UUID", "scaffold_shape", req.JobID)
		return
	}
	// sc-528 scaffold: cancellation has no jobs to cancel yet — sc-529 wires this
	// up to the actual container lifecycle. Idempotent 204 either way.
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleHealthz(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (s *Server) handleVersion(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.build)
}

// validateScaffoldShape catches the obviously-malformed cases at the scaffold
// stage. sc-530+ replace this with the full validator described in §10.2.
func validateScaffoldShape(req *RunRequest) error {
	if strings.TrimSpace(req.JobID) == "" {
		return errors.New("jobId must be set")
	}
	if _, err := uuid.Parse(req.JobID); err != nil {
		return fmt.Errorf("jobId must be a UUID: %w", err)
	}
	if strings.TrimSpace(req.TraceID) == "" {
		return errors.New("traceId must be set")
	}
	if strings.TrimSpace(req.RepoSlug) == "" {
		return errors.New("repoSlug must be set")
	}
	if strings.TrimSpace(req.Image) == "" {
		return errors.New("image must be set")
	}
	if len(req.Cmd) == 0 {
		return errors.New("cmd must contain at least one element")
	}
	if req.Limits.TimeoutSeconds <= 0 {
		return errors.New("limits.timeoutSeconds must be > 0")
	}
	return nil
}
