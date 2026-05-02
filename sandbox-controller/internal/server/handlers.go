package server

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
)

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

// RunResponse — for sc-528, the request fields plus a synthetic acceptedAt.
// sc-529 will populate exit code, captured streams, artifacts manifest.
type RunResponse struct {
	JobID      string    `json:"jobId"`
	AcceptedAt time.Time `json:"acceptedAt"`
	Echo       any       `json:"echo,omitempty"` // sc-528 only; removed in sc-529
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

	// sc-528 scaffold validation: just enough to reject obviously broken requests.
	// Real layer-2 validation (image whitelist, workspace path, limits caps,
	// privileged-arg rejection) lands in sc-530 / sc-531.
	if err := validateScaffoldShape(&req); err != nil {
		writeError(w, http.StatusBadRequest, "request_invalid", err.Error(), "scaffold_shape", req.JobID)
		return
	}

	resp := RunResponse{
		JobID:      req.JobID,
		AcceptedAt: time.Now().UTC(),
		Echo:       req,
	}
	writeJSON(w, http.StatusOK, resp)
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
