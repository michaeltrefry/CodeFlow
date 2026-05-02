package server

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"log/slog"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/apilog"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/auth"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/runner"
	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/whitelist"
)

// permissiveAllowlist is the allowlist setup tests use when they need /run to
// reach the runner. Allows ghcr.io/trefry/* and docker.io/library/alpine:*
// — covers every image used in tests that assert the happy path.
func permissiveAllowlist(t *testing.T) *whitelist.Allowlist {
	t.Helper()
	a, err := whitelist.Compile([]whitelist.Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "*"},
		{Registry: "docker.io", Repository: "library/alpine", Tag: "*"},
	})
	if err != nil {
		t.Fatal(err)
	}
	return a
}

// stubRunner is the minimal JobRunner the handler tests inject. Each test
// sets the result/err fields and the handler reflects them.
type stubRunner struct {
	gotSpec runner.JobSpec
	result  *runner.Result
	err     error
}

func (s *stubRunner) Run(ctx context.Context, spec runner.JobSpec) (*runner.Result, error) {
	s.gotSpec = spec
	return s.result, s.err
}

// testPKI is a self-contained mTLS PKI for tests: a CA, a server cert, and two
// client certs (one allowed, one not).
type testPKI struct {
	caCert     *x509.Certificate
	caKey      *ecdsa.PrivateKey
	serverPair tls.Certificate
	clientPool *x509.CertPool
}

func newTestPKI(t *testing.T) *testPKI {
	t.Helper()
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	caTmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "test-ca", Organization: []string{"trefry-test"}},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
	}
	caDER, err := x509.CreateCertificate(rand.Reader, caTmpl, caTmpl, &caKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	caCert, _ := x509.ParseCertificate(caDER)

	// Server cert (with localhost SAN so net/http TLS dialer is happy).
	serverKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	serverTmpl := &x509.Certificate{
		SerialNumber: big.NewInt(2),
		Subject:      pkix.Name{CommonName: "codeflow-sandbox-controller"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:     []string{"localhost"},
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1"), net.ParseIP("::1")},
	}
	serverDER, err := x509.CreateCertificate(rand.Reader, serverTmpl, caCert, &serverKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	serverPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: serverDER})
	serverKeyDER, _ := x509.MarshalECPrivateKey(serverKey)
	serverKeyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: serverKeyDER})
	serverPair, err := tls.X509KeyPair(serverPEM, serverKeyPEM)
	if err != nil {
		t.Fatal(err)
	}

	clientPool := x509.NewCertPool()
	clientPool.AddCert(caCert)

	return &testPKI{
		caCert:     caCert,
		caKey:      caKey,
		serverPair: serverPair,
		clientPool: clientPool,
	}
}

func (p *testPKI) issueClient(t *testing.T, cn string, orgs []string) tls.Certificate {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: cn, Organization: orgs},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, p.caCert, &key.PublicKey, p.caKey)
	if err != nil {
		t.Fatal(err)
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	keyDER, _ := x509.MarshalECPrivateKey(key)
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
	pair, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		t.Fatal(err)
	}
	return pair
}

// rootPoolFor returns a pool containing only the test CA, suitable for client-side
// server cert verification.
func (p *testPKI) rootPoolFor() *x509.CertPool {
	pool := x509.NewCertPool()
	pool.AddCert(p.caCert)
	return pool
}

// setupServer builds a server.Server with an allowlist of {codeflow-api, trefry}
// and starts an httptest TLS server in front of it. Returns the server URL,
// a cleanup function, and the stub runner the test can configure.
func setupServer(t *testing.T, pki *testPKI) (string, *stubRunner, func()) {
	t.Helper()
	cfg := &config.Config{
		Server:  config.ServerConfig{Listen: ":0"},
		Logging: config.LoggingConfig{Level: "error"},
	}
	verifier, err := auth.NewSubjectAllowlist([]config.SubjectPattern{
		{CommonName: "codeflow-api", Organization: "trefry"},
	})
	if err != nil {
		t.Fatal(err)
	}
	logger := apilog.New(slog.LevelError)
	stub := &stubRunner{}
	srv := New(cfg, verifier, BuildInfo{Commit: "test", BuildTime: "now"}, logger, stub, permissiveAllowlist(t))

	httpSrv := httptest.NewUnstartedServer(srv.Handler())
	httpSrv.TLS = &tls.Config{
		Certificates: []tls.Certificate{pki.serverPair},
		ClientCAs:    pki.clientPool,
		ClientAuth:   tls.RequireAndVerifyClientCert,
		MinVersion:   tls.VersionTLS13,
	}
	httpSrv.StartTLS()
	return httpSrv.URL, stub, httpSrv.Close
}

func clientWith(t *testing.T, pki *testPKI, clientPair tls.Certificate) *http.Client {
	t.Helper()
	return &http.Client{
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{
				RootCAs:      pki.rootPoolFor(),
				Certificates: []tls.Certificate{clientPair},
				MinVersion:   tls.VersionTLS13,
			},
		},
		Timeout: 5 * time.Second,
	}
}

func TestHealthz_RequiresMTLS(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()

	// Plaintext (no client cert) — handshake fails before any HTTP is exchanged.
	c := &http.Client{
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{RootCAs: pki.rootPoolFor(), MinVersion: tls.VersionTLS13},
		},
		Timeout: 5 * time.Second,
	}
	if _, err := c.Get(url + "/healthz"); err == nil {
		t.Fatal("expected TLS handshake error without client cert; got nil")
	}
}

func TestHealthz_AllowedClient(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()

	clientPair := pki.issueClient(t, "codeflow-api", []string{"trefry"})
	c := clientWith(t, pki, clientPair)

	resp, err := c.Get(url + "/healthz")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status: %d", resp.StatusCode)
	}
	body, _ := io.ReadAll(resp.Body)
	if !bytes.Contains(body, []byte(`"ok":true`)) {
		t.Fatalf("body: %q", body)
	}
}

func TestHealthz_DisallowedSubject(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()

	clientPair := pki.issueClient(t, "codeflow-ui", []string{"trefry"})
	c := clientWith(t, pki, clientPair)

	resp, err := c.Get(url + "/healthz")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("expected 403, got %d", resp.StatusCode)
	}
	body, _ := io.ReadAll(resp.Body)
	if !bytes.Contains(body, []byte(`"mtls_subject_not_allowed"`)) {
		t.Fatalf("expected mtls_subject_not_allowed error code, got %s", body)
	}
}

func TestVersion_ReturnsBuildInfo(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()

	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))
	resp, err := c.Get(url + "/version")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	var info BuildInfo
	if err := json.NewDecoder(resp.Body).Decode(&info); err != nil {
		t.Fatal(err)
	}
	if info.Commit != "test" {
		t.Fatalf("commit: %q", info.Commit)
	}
}

func TestRun_HappyPath(t *testing.T) {
	pki := newTestPKI(t)
	url, stub, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	jobID := uuid.NewString()
	stub.result = &runner.Result{
		JobID:      jobID,
		ExitCode:   0,
		Stdout:     "hi\n",
		Stderr:     "",
		DurationMs: 42,
	}

	req := RunRequest{
		JobID:    jobID,
		TraceID:  "abc123",
		RepoSlug: "trefry-codeflow-deadbeef",
		Image:    "ghcr.io/trefry/dotnet-tester:10.0",
		Cmd:      []string{"echo", "hi"},
		Limits: Limits{
			Cpus:           1.0,
			MemoryBytes:    1 << 30,
			Pids:           256,
			TimeoutSeconds: 60,
			StdoutMaxBytes: 1 << 20,
			StderrMaxBytes: 1 << 20,
		},
	}
	body, _ := json.Marshal(req)
	resp, err := c.Post(url+"/run", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("status %d: %s", resp.StatusCode, raw)
	}
	var got RunResponse
	if err := json.NewDecoder(resp.Body).Decode(&got); err != nil {
		t.Fatal(err)
	}
	if got.JobID != jobID {
		t.Errorf("jobId: %q", got.JobID)
	}
	if got.ExitCode != 0 || got.Stdout != "hi\n" {
		t.Errorf("body: %#v", got)
	}
	if got.DurationMs != 42 {
		t.Errorf("durationMs: %d", got.DurationMs)
	}

	// Verify the runner saw the spec we expected.
	if stub.gotSpec.JobID != jobID {
		t.Errorf("runner saw jobId %q", stub.gotSpec.JobID)
	}
	if stub.gotSpec.Image != req.Image {
		t.Errorf("runner saw image %q", stub.gotSpec.Image)
	}
	if stub.gotSpec.CPUs != 1.0 || stub.gotSpec.MemoryBytes != 1<<30 || stub.gotSpec.PidsLimit != 256 {
		t.Errorf("runner saw limits cpus=%v mem=%d pids=%d", stub.gotSpec.CPUs, stub.gotSpec.MemoryBytes, stub.gotSpec.PidsLimit)
	}
}

func TestRun_RunnerError(t *testing.T) {
	pki := newTestPKI(t)
	url, stub, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	stub.err = fmt.Errorf("simulated daemon failure")
	jobID := uuid.NewString()

	req := RunRequest{
		JobID:    jobID,
		TraceID:  "abc",
		RepoSlug: "r",
		Image:    "alpine:3",
		Cmd:      []string{"echo", "hi"},
		Limits:   Limits{TimeoutSeconds: 60},
	}
	body, _ := json.Marshal(req)
	resp, err := c.Post(url+"/run", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusInternalServerError {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 500, got %d: %s", resp.StatusCode, raw)
	}
	raw, _ := io.ReadAll(resp.Body)
	if !bytes.Contains(raw, []byte("daemon_error")) {
		t.Fatalf("expected daemon_error code, got %s", raw)
	}
}

func TestRun_RejectsUnknownField(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	body := []byte(fmt.Sprintf(`{
		"jobId": %q, "traceId": "abc", "repoSlug": "r", "image": "i",
		"cmd": ["x"], "limits": {"timeoutSeconds": 60},
		"privileged": true
	}`, uuid.NewString()))
	resp, err := c.Post(url+"/run", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 400, got %d: %s", resp.StatusCode, raw)
	}
	raw, _ := io.ReadAll(resp.Body)
	if !bytes.Contains(raw, []byte("privileged")) && !bytes.Contains(raw, []byte("unknown")) {
		t.Fatalf("expected unknown-field error, got %s", raw)
	}
}

func TestRun_RejectsMissingFields(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	cases := []struct {
		name string
		body []byte
	}{
		{"empty body", []byte(`{}`)},
		{"missing jobId", []byte(`{"traceId":"a","repoSlug":"r","image":"i","cmd":["x"],"limits":{"timeoutSeconds":1}}`)},
		{"non-uuid jobId", []byte(`{"jobId":"not-a-uuid","traceId":"a","repoSlug":"r","image":"i","cmd":["x"],"limits":{"timeoutSeconds":1}}`)},
		{"missing cmd", []byte(fmt.Sprintf(`{"jobId":%q,"traceId":"a","repoSlug":"r","image":"i","cmd":[],"limits":{"timeoutSeconds":1}}`, uuid.NewString()))},
		{"zero timeout", []byte(fmt.Sprintf(`{"jobId":%q,"traceId":"a","repoSlug":"r","image":"i","cmd":["x"],"limits":{"timeoutSeconds":0}}`, uuid.NewString()))},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			resp, err := c.Post(url+"/run", "application/json", bytes.NewReader(tc.body))
			if err != nil {
				t.Fatal(err)
			}
			defer resp.Body.Close()
			if resp.StatusCode != http.StatusBadRequest {
				raw, _ := io.ReadAll(resp.Body)
				t.Fatalf("expected 400, got %d: %s", resp.StatusCode, raw)
			}
		})
	}
}

func TestCancel_AcceptsValidJobID(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	body, _ := json.Marshal(CancelRequest{JobID: uuid.NewString()})
	resp, err := c.Post(url+"/cancel", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNoContent {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 204, got %d: %s", resp.StatusCode, raw)
	}
}

func TestCancel_RejectsBadJobID(t *testing.T) {
	pki := newTestPKI(t)
	url, _, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	body := []byte(`{"jobId": "not-a-uuid"}`)
	resp, err := c.Post(url+"/cancel", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 400, got %d: %s", resp.StatusCode, raw)
	}
}

func TestRun_RejectsImageNotOnAllowlist(t *testing.T) {
	pki := newTestPKI(t)
	url, stub, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	jobID := uuid.NewString()
	req := RunRequest{
		JobID:    jobID,
		TraceID:  "abc",
		RepoSlug: "r",
		// Not on the permissive allowlist used in setupServer.
		Image:  "attacker.example/evil:latest",
		Cmd:    []string{"sh", "-c", "exfiltrate"},
		Limits: Limits{TimeoutSeconds: 60},
	}
	body, _ := json.Marshal(req)
	resp, err := c.Post(url+"/run", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 403, got %d: %s", resp.StatusCode, raw)
	}
	raw, _ := io.ReadAll(resp.Body)
	if !bytes.Contains(raw, []byte("image_not_allowed")) {
		t.Fatalf("expected image_not_allowed code, got %s", raw)
	}
	// Critical: the runner must NOT have been invoked. A whitelist failure
	// has to bail before any docker call.
	if stub.gotSpec.JobID != "" {
		t.Errorf("runner was invoked for a non-allowlisted image: spec=%#v", stub.gotSpec)
	}
}

func TestServer_SetAllowlist_HotSwap(t *testing.T) {
	pki := newTestPKI(t)
	url, stub, cleanup := setupServer(t, pki)
	defer cleanup()
	c := clientWith(t, pki, pki.issueClient(t, "codeflow-api", []string{"trefry"}))

	// Default setup permits ghcr.io/trefry/dotnet-tester:* — replace it with
	// a strict allowlist that only permits docker.io/library/alpine:*. The
	// previous image now becomes forbidden.
	stub.result = &runner.Result{ExitCode: 0, Stdout: "ok\n"}

	// Direct access to the *Server isn't easy through httptest, but we can
	// build a fresh stack that points at a test server and SetAllowlist on it.
	cfg := &config.Config{
		Server:  config.ServerConfig{Listen: ":0"},
		Logging: config.LoggingConfig{Level: "error"},
	}
	verifier, err := auth.NewSubjectAllowlist([]config.SubjectPattern{
		{CommonName: "codeflow-api", Organization: "trefry"},
	})
	if err != nil {
		t.Fatal(err)
	}
	logger := apilog.New(slog.LevelError)
	stub2 := &stubRunner{result: &runner.Result{ExitCode: 0, Stdout: "ok\n"}}
	srv := New(cfg, verifier, BuildInfo{Commit: "t", BuildTime: "n"}, logger, stub2, permissiveAllowlist(t))

	// Hot-swap to a strict allowlist mid-flight.
	strict, err := whitelist.Compile([]whitelist.Rule{
		{Registry: "docker.io", Repository: "library/alpine", Tag: "*"},
	})
	if err != nil {
		t.Fatal(err)
	}
	srv.SetAllowlist(strict)

	httpSrv := httptest.NewUnstartedServer(srv.Handler())
	httpSrv.TLS = &tls.Config{
		Certificates: []tls.Certificate{pki.serverPair},
		ClientCAs:    pki.clientPool,
		ClientAuth:   tls.RequireAndVerifyClientCert,
		MinVersion:   tls.VersionTLS13,
	}
	httpSrv.StartTLS()
	defer httpSrv.Close()

	// ghcr.io/trefry/* was on the original permissive list but not on `strict`.
	body, _ := json.Marshal(RunRequest{
		JobID:    uuid.NewString(),
		TraceID:  "t",
		RepoSlug: "r",
		Image:    "ghcr.io/trefry/dotnet-tester:10.0",
		Cmd:      []string{"x"},
		Limits:   Limits{TimeoutSeconds: 60},
	})
	resp, err := c.Post(httpSrv.URL+"/run", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("expected 403 after hot-swap to strict allowlist, got %d: %s", resp.StatusCode, raw)
	}

	// alpine:3 is on `strict` — should pass.
	body2, _ := json.Marshal(RunRequest{
		JobID:    uuid.NewString(),
		TraceID:  "t",
		RepoSlug: "r",
		Image:    "alpine:3",
		Cmd:      []string{"echo", "hi"},
		Limits:   Limits{TimeoutSeconds: 60},
	})
	resp2, err := c.Post(httpSrv.URL+"/run", "application/json", bytes.NewReader(body2))
	if err != nil {
		t.Fatal(err)
	}
	defer resp2.Body.Close()
	if resp2.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(resp2.Body)
		t.Fatalf("expected 200 for alpine after swap, got %d: %s", resp2.StatusCode, raw)
	}
	_ = url // unused: this test builds its own httptest server
}
