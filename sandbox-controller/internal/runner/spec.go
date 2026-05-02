package runner

import (
	"fmt"
	"strings"
	"time"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/dockerd"
)

// JobSpec is the controller-internal representation of a job. It is built by
// the request handler from the wire-format RunRequest, after layer-2
// validation has applied the controller's caps. Fields not on this struct
// cannot be configured — see types.go in the dockerd package for the explicit
// "never set" list.
type JobSpec struct {
	JobID    string
	TraceID  string
	RepoSlug string

	Image string
	Cmd   []string
	Env   map[string]string

	// Resource limits. Already clamped to the controller's caps.
	CPUs           float64
	MemoryBytes    int64
	PidsLimit      int64
	TimeoutSeconds int

	// Output capture caps.
	StdoutMaxBytes int64
	StderrMaxBytes int64

	// Workspace paths — populated by sc-531's workspace.Validator. Empty when
	// the controller is configured without a workdir root (test-only path).
	WorkspaceHostPath string // mounted ro at /workspace
	ResultsHostPath   string // mounted rw at /artifacts; runner walks this for the manifest
}

// ContainerLabels are stamped on every spawned container so the cleanup
// sweep (sc-533) can find them.
func (s JobSpec) ContainerLabels() map[string]string {
	return map[string]string{
		"cf-managed":  "true",
		"cf.traceId":  s.TraceID,
		"cf.jobId":    s.JobID,
		"cf.repoSlug": s.RepoSlug,
	}
}

// ContainerName is what we pass as the docker container name. It includes the
// jobId prefix so it's findable by humans during incident response.
func (s JobSpec) ContainerName() string {
	if len(s.JobID) >= 8 {
		return "cf-job-" + s.JobID[:8]
	}
	return "cf-job-" + s.JobID
}

// SandboxUserUID is the user the controller forces every sandbox to run as.
// Independent of the agent-supplied image's USER directive.
const SandboxUserUID = "65534:65534"

// Sandbox container paths — fixed regardless of the agent's image, so build
// scripts have a stable contract.
const (
	WorkspaceContainerPath = "/workspace"
	ScratchContainerPath   = "/workspace/.scratch"
	ArtifactsContainerPath = "/artifacts"
)

// ToCreateRequest converts a JobSpec into the dockerd-level create request.
// This is the single place where the controller's locked-down container
// settings (runsc, no-network, read-only, no-new-privileges, cap_drop ALL,
// nonroot) are bolted on. Any future change to the security defaults goes
// here.
func (s JobSpec) ToCreateRequest() dockerd.CreateContainerRequest {
	env := make([]string, 0, len(s.Env))
	for k, v := range s.Env {
		env = append(env, fmt.Sprintf("%s=%s", k, v))
	}

	tmpfs := map[string]string{
		// Writable scratch inside the otherwise read-only filesystem.
		"/tmp": "size=64m,exec",
	}
	var bindMounts []dockerd.BindMount
	if s.WorkspaceHostPath != "" {
		// sc-531: workspace bind, read-only, with a tmpfs writable scratch
		// overlay at /workspace/.scratch so build outputs (bin/, obj/,
		// node_modules/, etc.) have somewhere to land per-job.
		bindMounts = append(bindMounts, dockerd.BindMount{
			HostPath:      s.WorkspaceHostPath,
			ContainerPath: WorkspaceContainerPath,
			ReadOnly:      true,
		})
		tmpfs[ScratchContainerPath] = "size=2g,exec"
	}
	if s.ResultsHostPath != "" {
		bindMounts = append(bindMounts, dockerd.BindMount{
			HostPath:      s.ResultsHostPath,
			ContainerPath: ArtifactsContainerPath,
			ReadOnly:      false,
		})
	}

	return dockerd.CreateContainerRequest{
		Image:       s.Image,
		Cmd:         s.Cmd,
		Env:         env,
		User:        SandboxUserUID,
		Runtime:     "runsc",
		NetworkMode: "none",
		CPUs:        s.CPUs,
		MemoryBytes: s.MemoryBytes,
		PidsLimit:   s.PidsLimit,
		Tmpfs:       tmpfs,
		BindMounts:  bindMounts,
		Labels:      s.ContainerLabels(),
	}
}

// Timeout returns the JobSpec's timeout as a time.Duration.
func (s JobSpec) Timeout() time.Duration {
	return time.Duration(s.TimeoutSeconds) * time.Second
}

// Validate is a final sanity check on the JobSpec. It is intentionally narrow —
// the request handler / config layer apply the policy clamps. This method just
// catches obviously-broken specs that would crash the runner.
func (s JobSpec) Validate() error {
	if strings.TrimSpace(s.JobID) == "" {
		return fmt.Errorf("jobId required")
	}
	if strings.TrimSpace(s.Image) == "" {
		return fmt.Errorf("image required")
	}
	if len(s.Cmd) == 0 {
		return fmt.Errorf("cmd required")
	}
	if s.TimeoutSeconds <= 0 {
		return fmt.Errorf("timeout must be > 0")
	}
	if s.MemoryBytes < 0 || s.PidsLimit < 0 || s.CPUs < 0 {
		return fmt.Errorf("limits must be non-negative")
	}
	return nil
}
