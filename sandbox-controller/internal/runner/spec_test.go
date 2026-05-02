package runner

import (
	"strings"
	"testing"
)

func TestJobSpec_ContainerName(t *testing.T) {
	s := JobSpec{JobID: "01951f8d-0123-7abc-89ab-cdef00112233"}
	got := s.ContainerName()
	if !strings.HasPrefix(got, "cf-job-") {
		t.Fatalf("expected cf-job- prefix, got %q", got)
	}
	if len(got) != len("cf-job-")+8 {
		t.Fatalf("expected 8-char id suffix, got %q", got)
	}
}

func TestJobSpec_Labels(t *testing.T) {
	s := JobSpec{JobID: "j1", TraceID: "t1", RepoSlug: "r1"}
	labels := s.ContainerLabels()
	for _, k := range []string{"cf-managed", "cf.traceId", "cf.jobId", "cf.repoSlug"} {
		if _, ok := labels[k]; !ok {
			t.Errorf("label %q missing", k)
		}
	}
	if labels["cf-managed"] != "true" {
		t.Error("cf-managed must be 'true'")
	}
}

func TestJobSpec_ToCreateRequest_LocksDownDefaults(t *testing.T) {
	s := JobSpec{
		JobID:          "j1",
		Image:          "alpine:3",
		Cmd:            []string{"echo", "hi"},
		CPUs:           2.0,
		MemoryBytes:    1 << 30,
		PidsLimit:      512,
		TimeoutSeconds: 60,
	}
	req := s.ToCreateRequest()

	if req.Runtime != "runsc" {
		t.Errorf("Runtime: %q", req.Runtime)
	}
	if req.NetworkMode != "none" {
		t.Errorf("NetworkMode: %q", req.NetworkMode)
	}
	if req.User != SandboxUserUID {
		t.Errorf("User: %q (want %q)", req.User, SandboxUserUID)
	}
	if _, ok := req.Tmpfs["/tmp"]; !ok {
		t.Error("expected /tmp tmpfs entry")
	}
	if req.CPUs != 2.0 || req.MemoryBytes != 1<<30 || req.PidsLimit != 512 {
		t.Errorf("limits not propagated: cpus=%v mem=%d pids=%d", req.CPUs, req.MemoryBytes, req.PidsLimit)
	}
}

func TestJobSpec_Validate(t *testing.T) {
	good := JobSpec{
		JobID:          "j1",
		Image:          "alpine:3",
		Cmd:            []string{"echo", "hi"},
		TimeoutSeconds: 60,
	}
	if err := good.Validate(); err != nil {
		t.Fatalf("expected nil, got %v", err)
	}

	bad := []struct {
		name string
		s    JobSpec
	}{
		{"missing jobId", JobSpec{Image: "alpine", Cmd: []string{"x"}, TimeoutSeconds: 1}},
		{"missing image", JobSpec{JobID: "j", Cmd: []string{"x"}, TimeoutSeconds: 1}},
		{"missing cmd", JobSpec{JobID: "j", Image: "alpine", TimeoutSeconds: 1}},
		{"zero timeout", JobSpec{JobID: "j", Image: "alpine", Cmd: []string{"x"}}},
		{"negative memory", JobSpec{JobID: "j", Image: "alpine", Cmd: []string{"x"}, TimeoutSeconds: 1, MemoryBytes: -1}},
	}
	for _, tc := range bad {
		t.Run(tc.name, func(t *testing.T) {
			if err := tc.s.Validate(); err == nil {
				t.Fatal("expected error")
			}
		})
	}
}
