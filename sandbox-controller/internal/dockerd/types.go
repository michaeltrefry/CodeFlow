package dockerd

import (
	"encoding/json"
	"fmt"
)

// containerCreatePayload is the JSON shape POST /containers/create expects.
// We expose only the fields the controller intentionally sets — every other
// docker option is left at the daemon default. Notably absent (defense in depth):
//
//	Privileged           — controller never sets this
//	NetworkMode "host"   — only "none" or "bridge" allowed at runner layer
//	PidMode  "host"      — never set
//	IpcMode  "host"      — never set
//	UsernsMode           — never set
//	CapAdd               — never set
//	Devices              — never set
//	Mounts (other than tmpfs) — sc-531 adds workspace mount; nothing else permitted
//	PublishAllPorts      — never set
//	ExposedPorts         — never set
//	PortBindings         — never set
type containerCreatePayload struct {
	Image        string                  `json:"Image"`
	Cmd          []string                `json:"Cmd,omitempty"`
	Env          []string                `json:"Env,omitempty"`
	User         string                  `json:"User,omitempty"`
	WorkingDir   string                  `json:"WorkingDir,omitempty"`
	Tty          bool                    `json:"Tty"`
	OpenStdin    bool                    `json:"OpenStdin"`
	AttachStdout bool                    `json:"AttachStdout"`
	AttachStderr bool                    `json:"AttachStderr"`
	Labels       map[string]string       `json:"Labels,omitempty"`
	HostConfig   containerHostConfig     `json:"HostConfig"`
	NetworkConfig containerNetworkConfig `json:"NetworkingConfig"`
}

type containerHostConfig struct {
	Runtime        string            `json:"Runtime,omitempty"`
	NetworkMode    string            `json:"NetworkMode,omitempty"`
	ReadonlyRootfs bool              `json:"ReadonlyRootfs"`
	CapDrop        []string          `json:"CapDrop,omitempty"`
	SecurityOpt    []string          `json:"SecurityOpt,omitempty"`
	Tmpfs          map[string]string `json:"Tmpfs,omitempty"`
	Mounts         []containerMount  `json:"Mounts,omitempty"`

	// Resource limits — docker uses the cgroups cpu period/quota model and
	// memory in bytes. CPUs (fractional) is converted via period=100000.
	CPUPeriod int64 `json:"CpuPeriod,omitempty"`
	CPUQuota  int64 `json:"CpuQuota,omitempty"`
	Memory    int64 `json:"Memory,omitempty"`
	PidsLimit int64 `json:"PidsLimit,omitempty"`

	// Restart policy — never. The controller's job is to run-once.
	RestartPolicy struct {
		Name string `json:"Name"`
	} `json:"RestartPolicy"`

	// AutoRemove is FALSE so the controller can read logs after exit. The
	// controller's runner explicitly removes the container after capture.
	AutoRemove bool `json:"AutoRemove"`
}

// containerMount is the docker Engine API "Mounts" array element (Type=bind).
type containerMount struct {
	Type     string `json:"Type"`     // always "bind"
	Source   string `json:"Source"`   // host path (validated by workspace.Validator)
	Target   string `json:"Target"`   // path inside the container
	ReadOnly bool   `json:"ReadOnly"`
}

type containerNetworkConfig struct {
	// EndpointsConfig is intentionally empty — combined with NetworkMode=none
	// the container has no network at all.
	EndpointsConfig map[string]any `json:"EndpointsConfig,omitempty"`
}

// buildCreateBody is the intentionally-narrow construction site where
// CreateContainerRequest becomes a Docker create payload. Anything not on
// CreateContainerRequest is forced to the safe default.
func buildCreateBody(req CreateContainerRequest) containerCreatePayload {
	cpuPeriod := int64(100000)
	cpuQuota := int64(0)
	if req.CPUs > 0 {
		cpuQuota = int64(req.CPUs * float64(cpuPeriod))
	}

	return containerCreatePayload{
		Image:        req.Image,
		Cmd:          req.Cmd,
		Env:          req.Env,
		User:         req.User,
		WorkingDir:   req.WorkingDir,
		Tty:          false,
		OpenStdin:    false,
		AttachStdout: true,
		AttachStderr: true,
		Labels:       req.Labels,
		HostConfig: containerHostConfig{
			Runtime:        req.Runtime,
			NetworkMode:    req.NetworkMode,
			ReadonlyRootfs: true,
			CapDrop:        []string{"ALL"},
			SecurityOpt:    []string{"no-new-privileges:true"},
			Tmpfs:          req.Tmpfs,
			Mounts:         buildMounts(req.BindMounts),
			CPUPeriod:      cpuPeriod,
			CPUQuota:       cpuQuota,
			Memory:         req.MemoryBytes,
			PidsLimit:      req.PidsLimit,
			AutoRemove:     false,
			RestartPolicy: struct {
				Name string `json:"Name"`
			}{Name: "no"},
		},
	}
}

func buildMounts(binds []BindMount) []containerMount {
	if len(binds) == 0 {
		return nil
	}
	out := make([]containerMount, 0, len(binds))
	for _, b := range binds {
		out = append(out, containerMount{
			Type:     "bind",
			Source:   b.HostPath,
			Target:   b.ContainerPath,
			ReadOnly: b.ReadOnly,
		})
	}
	return out
}

// MarshalJSON is provided so callers (and tests) can inspect the wire format
// without using internal types directly.
func (p containerCreatePayload) MarshalJSON() ([]byte, error) {
	type alias containerCreatePayload
	return json.Marshal(alias(p))
}

// String is for debug/logging — not for production logs (would leak Cmd/Env).
func (p containerCreatePayload) String() string {
	return fmt.Sprintf("create{image=%s,cmd=%v,user=%s,runtime=%s,network=%s,readonly=%t,memory=%d,pids=%d}",
		p.Image, p.Cmd, p.User, p.HostConfig.Runtime, p.HostConfig.NetworkMode,
		p.HostConfig.ReadonlyRootfs, p.HostConfig.Memory, p.HostConfig.PidsLimit)
}
