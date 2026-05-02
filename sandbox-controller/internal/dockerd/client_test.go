package dockerd

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func newTestClient(handler http.Handler) (*Client, *httptest.Server) {
	srv := httptest.NewServer(handler)
	c := NewWithTransport(http.DefaultTransport, strings.TrimPrefix(srv.URL, "http://"))
	return c, srv
}

func TestPing(t *testing.T) {
	c, srv := newTestClient(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1.45/_ping" {
			t.Errorf("unexpected path: %s", r.URL.Path)
		}
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("OK"))
	}))
	defer srv.Close()
	if err := c.Ping(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestCreateContainer_LocksDownHostConfig(t *testing.T) {
	var captured containerCreatePayload
	c, srv := newTestClient(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1.45/containers/create" {
			t.Errorf("path: %s", r.URL.Path)
		}
		if got := r.URL.Query().Get("name"); got != "cf-job-x" {
			t.Errorf("name: %q", got)
		}
		if err := json.NewDecoder(r.Body).Decode(&captured); err != nil {
			t.Fatal(err)
		}
		w.WriteHeader(http.StatusCreated)
		_ = json.NewEncoder(w).Encode(CreateContainerResponse{ID: "abc123"})
	}))
	defer srv.Close()

	resp, err := c.CreateContainer(context.Background(), "cf-job-x", CreateContainerRequest{
		Image:       "alpine:3",
		Cmd:         []string{"echo", "hi"},
		User:        "65534:65534",
		Runtime:     "runsc",
		NetworkMode: "none",
		CPUs:        2.0,
		MemoryBytes: 4 * 1024 * 1024 * 1024,
		PidsLimit:   1024,
		Tmpfs:       map[string]string{"/tmp": "size=64m"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if resp.ID != "abc123" {
		t.Fatalf("id: %q", resp.ID)
	}

	// The wire format must lock down the dangerous fields, not echo what the
	// caller passed.
	if captured.HostConfig.ReadonlyRootfs != true {
		t.Errorf("ReadonlyRootfs must be true, got %v", captured.HostConfig.ReadonlyRootfs)
	}
	if got := captured.HostConfig.CapDrop; len(got) != 1 || got[0] != "ALL" {
		t.Errorf("CapDrop must be [ALL], got %v", got)
	}
	if got := captured.HostConfig.SecurityOpt; len(got) != 1 || got[0] != "no-new-privileges:true" {
		t.Errorf("SecurityOpt must include no-new-privileges, got %v", got)
	}
	if got := captured.HostConfig.RestartPolicy.Name; got != "no" {
		t.Errorf("RestartPolicy must be no, got %q", got)
	}
	if captured.HostConfig.AutoRemove {
		t.Error("AutoRemove must be false (controller removes after log capture)")
	}
	if captured.HostConfig.Runtime != "runsc" {
		t.Errorf("Runtime: %q", captured.HostConfig.Runtime)
	}
	if captured.HostConfig.NetworkMode != "none" {
		t.Errorf("NetworkMode: %q", captured.HostConfig.NetworkMode)
	}
	if captured.HostConfig.CPUQuota != 200000 || captured.HostConfig.CPUPeriod != 100000 {
		t.Errorf("CPU quota/period: %d/%d (want 200000/100000)", captured.HostConfig.CPUQuota, captured.HostConfig.CPUPeriod)
	}
	if captured.HostConfig.Memory != 4*1024*1024*1024 {
		t.Errorf("Memory: %d", captured.HostConfig.Memory)
	}
	if captured.HostConfig.PidsLimit != 1024 {
		t.Errorf("PidsLimit: %d", captured.HostConfig.PidsLimit)
	}
}

func TestStartWaitRemove(t *testing.T) {
	mux := http.NewServeMux()
	mux.HandleFunc("/v1.45/containers/abc/start", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	})
	mux.HandleFunc("/v1.45/containers/abc/wait", func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(WaitContainerResponse{StatusCode: 0})
	})
	mux.HandleFunc("/v1.45/containers/abc", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodDelete {
			t.Errorf("delete method: %s", r.Method)
		}
		w.WriteHeader(http.StatusNoContent)
	})
	c, srv := newTestClient(mux)
	defer srv.Close()

	if err := c.StartContainer(context.Background(), "abc"); err != nil {
		t.Fatal(err)
	}
	wait, err := c.WaitContainer(context.Background(), "abc")
	if err != nil {
		t.Fatal(err)
	}
	if wait.StatusCode != 0 {
		t.Fatalf("status: %d", wait.StatusCode)
	}
	if err := c.RemoveContainer(context.Background(), "abc", true); err != nil {
		t.Fatal(err)
	}
}

func TestErrorEnvelope(t *testing.T) {
	c, srv := newTestClient(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNotFound)
		_ = json.NewEncoder(w).Encode(map[string]string{"message": "no such container: abc"})
	}))
	defer srv.Close()

	err := c.StartContainer(context.Background(), "abc")
	if err == nil {
		t.Fatal("expected error")
	}
	var de *Error
	if !errAs(err, &de) {
		t.Fatalf("expected *Error, got %T", err)
	}
	if de.Status != http.StatusNotFound {
		t.Errorf("status: %d", de.Status)
	}
	if !strings.Contains(de.Message, "no such container") {
		t.Errorf("message: %q", de.Message)
	}
	if !IsNotFound(err) {
		t.Error("IsNotFound should be true for 404")
	}
}

func TestSplitImageRef(t *testing.T) {
	cases := []struct {
		in        string
		repo, tag string
	}{
		{"alpine", "alpine", "latest"},
		{"alpine:3", "alpine", "3"},
		{"ghcr.io/trefry/dotnet-tester:10.0", "ghcr.io/trefry/dotnet-tester", "10.0"},
		{"registry:5000/image", "registry:5000/image", "latest"},
		{"registry:5000/image:tag", "registry:5000/image", "tag"},
	}
	for _, tc := range cases {
		t.Run(tc.in, func(t *testing.T) {
			repo, tag := splitImageRef(tc.in)
			if repo != tc.repo || tag != tc.tag {
				t.Errorf("got (%q,%q), want (%q,%q)", repo, tag, tc.repo, tc.tag)
			}
		})
	}
}

func TestDemux(t *testing.T) {
	frame := func(stream byte, payload string) []byte {
		hdr := make([]byte, 8)
		hdr[0] = stream
		binary.BigEndian.PutUint32(hdr[4:], uint32(len(payload)))
		return append(hdr, []byte(payload)...)
	}
	stream := bytesJoin(
		frame(1, "hi\n"),
		frame(2, "warning\n"),
		frame(1, "again\n"),
	)
	var stdout, stderr strings.Builder
	if err := Demux(strings.NewReader(string(stream)), &stdout, &stderr); err != nil {
		t.Fatal(err)
	}
	if stdout.String() != "hi\nagain\n" {
		t.Errorf("stdout: %q", stdout.String())
	}
	if stderr.String() != "warning\n" {
		t.Errorf("stderr: %q", stderr.String())
	}
}

// bytesJoin avoids importing bytes for this single-use helper.
func bytesJoin(parts ...[]byte) []byte {
	var n int
	for _, p := range parts {
		n += len(p)
	}
	out := make([]byte, 0, n)
	for _, p := range parts {
		out = append(out, p...)
	}
	return out
}

// errAs is a tiny errors.As wrapper kept local to avoid a separate import.
func errAs(err error, target **Error) bool {
	for err != nil {
		if e, ok := err.(*Error); ok {
			*target = e
			return true
		}
		type unwrapper interface{ Unwrap() error }
		u, ok := err.(unwrapper)
		if !ok {
			return false
		}
		err = u.Unwrap()
	}
	return false
}

// quiet "unused" linter for io
var _ = io.Discard
