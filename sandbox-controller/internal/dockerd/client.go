// Package dockerd is a minimal HTTP client for the Docker Engine API over a
// unix socket. It is intentionally hand-rolled rather than pulling in moby's
// full Go SDK — the controller talks to docker through ~7 endpoints and
// keeping the dependency surface tiny is part of the "minimal attack surface"
// hardening posture from sc-538.
//
// The Docker Engine API is documented at:
//
//	https://docs.docker.com/engine/api/v1.45/
//
// All methods take a context for cancellation. Errors include the HTTP status
// code and a parsed `{"message":"..."}` body when the daemon returns one.
package dockerd

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// DefaultAPIVersion pins the Engine API version. v1.45 is supported by Docker
// Engine 26+; gVisor (runsc) is registered as a runtime via daemon.json which
// is independent of API version.
const DefaultAPIVersion = "v1.45"

// DefaultSocketPath is the canonical path to the local docker daemon's unix
// socket on Linux hosts.
const DefaultSocketPath = "/var/run/docker.sock"

// Client is a thin HTTP client over a unix socket to a local docker daemon.
type Client struct {
	httpClient *http.Client
	apiVersion string

	// host is what we put in the URL host position. The actual transport
	// dials the unix socket; the host string is irrelevant on the wire but
	// must be syntactically valid for url.Parse.
	host string
}

// New returns a Client dialed at the given socket path. The Client uses no
// network — http.Transport.DialContext is overridden to dial the unix socket
// regardless of the request host.
func New(socketPath string) *Client {
	if socketPath == "" {
		socketPath = DefaultSocketPath
	}
	transport := &http.Transport{
		DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
			d := net.Dialer{Timeout: 5 * time.Second}
			return d.DialContext(ctx, "unix", socketPath)
		},
		// No connection pooling games — the docker socket is local and cheap.
		MaxIdleConns:          4,
		IdleConnTimeout:       30 * time.Second,
		ResponseHeaderTimeout: 0, // /containers/{id}/wait can block for the job's lifetime
	}
	return &Client{
		httpClient: &http.Client{Transport: transport},
		apiVersion: DefaultAPIVersion,
		host:       "docker",
	}
}

// NewWithTransport returns a Client backed by an arbitrary http.RoundTripper.
// Used in tests to swap in an httptest server.
func NewWithTransport(rt http.RoundTripper, host string) *Client {
	return &Client{
		httpClient: &http.Client{Transport: rt},
		apiVersion: DefaultAPIVersion,
		host:       host,
	}
}

// Ping calls GET /_ping; returns nil if the daemon answers.
func (c *Client) Ping(ctx context.Context) error {
	req, err := c.newRequest(ctx, http.MethodGet, "/_ping", nil, nil)
	if err != nil {
		return err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("docker ping: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return c.errorFromResponse(resp, "ping")
	}
	return nil
}

// CreateContainerRequest is the subset of the docker create config surfaced
// to runner code. Fields not on this struct are not configurable — defense in
// depth: callers cannot set --privileged, host network, host PID, additional
// capabilities, or arbitrary mounts even by reaching into the dockerd client
// directly.
type CreateContainerRequest struct {
	Image       string
	Cmd         []string
	Env         []string // KEY=VALUE
	User        string
	WorkingDir  string
	Labels      map[string]string
	Runtime     string // "runsc" for gVisor
	NetworkMode string // "none" by default
	CPUs        float64
	MemoryBytes int64
	PidsLimit   int64
	Tmpfs       map[string]string // path -> options (e.g. "/tmp" -> "size=64m,exec")
}

// CreateContainerResponse matches Docker's POST /containers/create response.
type CreateContainerResponse struct {
	ID       string   `json:"Id"`
	Warnings []string `json:"Warnings"`
}

// CreateContainer submits a container-create call with the locked-down
// hostConfig the controller always applies. Spec defaults that the controller
// owns (read-only rootfs, cap_drop ALL, no-new-privileges, no published ports)
// are baked into the wire format here so callers cannot disable them.
func (c *Client) CreateContainer(ctx context.Context, name string, req CreateContainerRequest) (*CreateContainerResponse, error) {
	body := buildCreateBody(req)
	q := url.Values{}
	if name != "" {
		q.Set("name", name)
	}
	httpReq, err := c.newRequest(ctx, http.MethodPost, "/containers/create?"+q.Encode(), nil, body)
	if err != nil {
		return nil, err
	}
	resp, err := c.httpClient.Do(httpReq)
	if err != nil {
		return nil, fmt.Errorf("docker container create: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusCreated {
		return nil, c.errorFromResponse(resp, "container create")
	}
	var out CreateContainerResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		return nil, fmt.Errorf("docker container create: decode response: %w", err)
	}
	return &out, nil
}

// StartContainer calls POST /containers/{id}/start.
func (c *Client) StartContainer(ctx context.Context, id string) error {
	req, err := c.newRequest(ctx, http.MethodPost, "/containers/"+id+"/start", nil, nil)
	if err != nil {
		return err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("docker container start: %w", err)
	}
	defer resp.Body.Close()
	// 204 No Content on success; 304 if already running.
	if resp.StatusCode != http.StatusNoContent && resp.StatusCode != http.StatusNotModified {
		return c.errorFromResponse(resp, "container start")
	}
	return nil
}

// WaitContainerResponse is the JSON body of a wait response.
type WaitContainerResponse struct {
	StatusCode int64                       `json:"StatusCode"`
	Error      *WaitContainerResponseError `json:"Error,omitempty"`
}

// WaitContainerResponseError is the optional error block in a wait response.
type WaitContainerResponseError struct {
	Message string `json:"Message"`
}

// WaitContainer blocks until the container exits, then returns the exit code.
// Honors ctx — if ctx is cancelled the request is aborted (the caller should
// then KillContainer separately).
func (c *Client) WaitContainer(ctx context.Context, id string) (*WaitContainerResponse, error) {
	req, err := c.newRequest(ctx, http.MethodPost, "/containers/"+id+"/wait?condition=not-running", nil, nil)
	if err != nil {
		return nil, err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("docker container wait: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, c.errorFromResponse(resp, "container wait")
	}
	var out WaitContainerResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		return nil, fmt.Errorf("docker container wait: decode response: %w", err)
	}
	return &out, nil
}

// ContainerLogs reads the container's stdout/stderr stream after exit.
//
// Docker returns a multiplexed stream when the container is not started in TTY
// mode (which we never do). The caller should pass the result to dockerd.Demux
// to split it into stdout and stderr.
//
// follow=false because we only call this after WaitContainer has returned —
// the container has exited and all logs are available immediately.
func (c *Client) ContainerLogs(ctx context.Context, id string) (io.ReadCloser, error) {
	q := url.Values{}
	q.Set("stdout", "1")
	q.Set("stderr", "1")
	q.Set("follow", "0")
	q.Set("timestamps", "0")
	req, err := c.newRequest(ctx, http.MethodGet, "/containers/"+id+"/logs?"+q.Encode(), nil, nil)
	if err != nil {
		return nil, err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("docker container logs: %w", err)
	}
	if resp.StatusCode != http.StatusOK {
		defer resp.Body.Close()
		return nil, c.errorFromResponse(resp, "container logs")
	}
	return resp.Body, nil
}

// KillContainer sends a signal to a running container.
func (c *Client) KillContainer(ctx context.Context, id, signal string) error {
	q := url.Values{}
	if signal != "" {
		q.Set("signal", signal)
	}
	req, err := c.newRequest(ctx, http.MethodPost, "/containers/"+id+"/kill?"+q.Encode(), nil, nil)
	if err != nil {
		return err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("docker container kill: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent && resp.StatusCode != http.StatusNotFound {
		return c.errorFromResponse(resp, "container kill")
	}
	return nil
}

// RemoveContainer deletes a container. Force=true to remove a still-running
// container; v=true to also remove anonymous volumes.
func (c *Client) RemoveContainer(ctx context.Context, id string, force bool) error {
	q := url.Values{}
	if force {
		q.Set("force", "1")
	}
	q.Set("v", "1")
	req, err := c.newRequest(ctx, http.MethodDelete, "/containers/"+id+"?"+q.Encode(), nil, nil)
	if err != nil {
		return err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("docker container remove: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent && resp.StatusCode != http.StatusNotFound {
		return c.errorFromResponse(resp, "container remove")
	}
	return nil
}

// ImagePull issues POST /images/create?fromImage=...&tag=... and drains the
// progress stream. Image references can be `image:tag` or
// `registry/owner/image:tag`. Returns when the daemon closes the stream.
func (c *Client) ImagePull(ctx context.Context, ref string) error {
	repo, tag := splitImageRef(ref)
	q := url.Values{}
	q.Set("fromImage", repo)
	if tag != "" {
		q.Set("tag", tag)
	}
	req, err := c.newRequest(ctx, http.MethodPost, "/images/create?"+q.Encode(), nil, nil)
	if err != nil {
		return err
	}
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("docker image pull: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return c.errorFromResponse(resp, "image pull")
	}
	// Drain the progress stream — docker streams JSON lines; an `errorDetail`
	// line means the pull failed even though the HTTP status is 200.
	dec := json.NewDecoder(resp.Body)
	for {
		var msg struct {
			ErrorDetail *struct {
				Message string `json:"message"`
			} `json:"errorDetail,omitempty"`
		}
		if err := dec.Decode(&msg); err != nil {
			if err == io.EOF {
				return nil
			}
			return fmt.Errorf("docker image pull: stream decode: %w", err)
		}
		if msg.ErrorDetail != nil {
			return fmt.Errorf("docker image pull: %s", msg.ErrorDetail.Message)
		}
	}
}

// --- internals -------------------------------------------------------------

func (c *Client) newRequest(ctx context.Context, method, path string, headers map[string]string, body any) (*http.Request, error) {
	var reader io.Reader
	if body != nil {
		buf, err := json.Marshal(body)
		if err != nil {
			return nil, fmt.Errorf("marshal body: %w", err)
		}
		reader = bytes.NewReader(buf)
	}
	u := fmt.Sprintf("http://%s/%s%s", c.host, c.apiVersion, path)
	req, err := http.NewRequestWithContext(ctx, method, u, reader)
	if err != nil {
		return nil, err
	}
	if reader != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	return req, nil
}

// errorFromResponse parses the daemon's `{"message":"..."}` body and returns
// a typed Error with the HTTP status code and message attached.
func (c *Client) errorFromResponse(resp *http.Response, op string) error {
	var msg struct {
		Message string `json:"message"`
	}
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 8192))
	_ = json.Unmarshal(body, &msg)
	return &Error{
		Op:      op,
		Status:  resp.StatusCode,
		Message: strings.TrimSpace(msg.Message),
		Body:    strings.TrimSpace(string(body)),
	}
}

// Error is returned for non-2xx Docker responses.
type Error struct {
	Op      string
	Status  int
	Message string
	Body    string
}

func (e *Error) Error() string {
	if e.Message != "" {
		return fmt.Sprintf("docker %s: %d: %s", e.Op, e.Status, e.Message)
	}
	return fmt.Sprintf("docker %s: %d: %s", e.Op, e.Status, e.Body)
}

// IsNotFound reports whether the error represents a 404 (container/image not found).
func IsNotFound(err error) bool {
	var de *Error
	for {
		if e, ok := err.(*Error); ok {
			de = e
			break
		}
		// unwrap; Go 1.20+
		type unwrapper interface{ Unwrap() error }
		u, ok := err.(unwrapper)
		if !ok {
			return false
		}
		err = u.Unwrap()
		if err == nil {
			return false
		}
	}
	return de.Status == http.StatusNotFound
}

func splitImageRef(ref string) (repo, tag string) {
	// Find the last colon that isn't part of a registry port (e.g. registry:5000/image:tag).
	// Heuristic: if there's a slash after the last colon, the colon is part of the registry.
	if idx := strings.LastIndex(ref, ":"); idx > 0 {
		if !strings.Contains(ref[idx:], "/") {
			return ref[:idx], ref[idx+1:]
		}
	}
	return ref, "latest"
}
