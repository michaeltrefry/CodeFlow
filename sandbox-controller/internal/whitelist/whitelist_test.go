package whitelist

import (
	"errors"
	"testing"
)

func mustCompile(t *testing.T, rules []Rule) *Allowlist {
	t.Helper()
	a, err := Compile(rules)
	if err != nil {
		t.Fatalf("compile: %v", err)
	}
	return a
}

func TestCompile_Empty(t *testing.T) {
	a, err := Compile(nil)
	if err != nil {
		t.Fatalf("compile nil: %v", err)
	}
	if a.Count() != 0 {
		t.Fatalf("count: %d", a.Count())
	}
	// Default-deny on empty allowlist.
	err = a.Verify("alpine:3")
	if !errors.Is(err, ErrImageNotAllowed) {
		t.Fatalf("expected ErrImageNotAllowed, got %v", err)
	}
}

func TestCompile_RejectsBlankRepo(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "", Tag: "1"}}); err == nil {
		t.Fatal("expected error for blank repository")
	}
}

func TestCompile_RejectsMultipleStars(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "x/y", Tag: "*-*"}}); err == nil {
		t.Fatal("expected error for two '*' in tag")
	}
}

func TestCompile_RejectsLeadingStar(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "x/y", Tag: "*-foo"}}); err == nil {
		t.Fatal("expected error for non-trailing '*'")
	}
}

func TestVerify_ExactMatch(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "10.0-sdk-alpine"},
	})
	cases := []struct {
		image   string
		wantErr bool
	}{
		{"ghcr.io/trefry/dotnet-tester:10.0-sdk-alpine", false},
		{"ghcr.io/trefry/dotnet-tester:10.0-sdk-debian", true},
		{"ghcr.io/other/dotnet-tester:10.0-sdk-alpine", true},
		{"docker.io/trefry/dotnet-tester:10.0-sdk-alpine", true},
	}
	for _, tc := range cases {
		t.Run(tc.image, func(t *testing.T) {
			err := a.Verify(tc.image)
			if tc.wantErr && err == nil {
				t.Errorf("expected error, got nil")
			}
			if !tc.wantErr && err != nil {
				t.Errorf("expected nil, got %v", err)
			}
		})
	}
}

func TestVerify_TrailingWildcard(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "10.0-sdk-*"},
	})
	cases := []struct {
		image   string
		wantErr bool
	}{
		{"ghcr.io/trefry/dotnet-tester:10.0-sdk-alpine", false},
		{"ghcr.io/trefry/dotnet-tester:10.0-sdk-debian", false},
		{"ghcr.io/trefry/dotnet-tester:10.0-sdk-", false},
		{"ghcr.io/trefry/dotnet-tester:10.0-runtime-alpine", true},
	}
	for _, tc := range cases {
		t.Run(tc.image, func(t *testing.T) {
			err := a.Verify(tc.image)
			if tc.wantErr && err == nil {
				t.Errorf("expected error, got nil")
			}
			if !tc.wantErr && err != nil {
				t.Errorf("expected nil, got %v", err)
			}
		})
	}
}

func TestVerify_CatchAllStar(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/sandbox-base", Tag: "*"},
	})
	if err := a.Verify("ghcr.io/trefry/sandbox-base:any-tag-here"); err != nil {
		t.Errorf("'*' should match anything, got %v", err)
	}
	if err := a.Verify("ghcr.io/trefry/sandbox-base:latest"); err != nil {
		t.Errorf("'*' should match 'latest', got %v", err)
	}
	if err := a.Verify("ghcr.io/different/image:tag"); err == nil {
		t.Errorf("expected reject for non-matching repo")
	}
}

func TestVerify_DefaultsToLatest(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "latest"},
	})
	if err := a.Verify("ghcr.io/trefry/dotnet-tester"); err != nil {
		t.Errorf("bare ref should default to :latest and match, got %v", err)
	}
}

func TestVerify_DigestStrippedToLatest(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "latest"},
	})
	if err := a.Verify("ghcr.io/trefry/dotnet-tester@sha256:abc"); err != nil {
		t.Errorf("digest should match latest rule for v1, got %v", err)
	}
}

func TestVerify_DockerHubLibrary(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "docker.io", Repository: "library/alpine", Tag: "3"},
	})
	if err := a.Verify("alpine:3"); err != nil {
		t.Errorf("bare 'alpine:3' should resolve to docker.io/library/alpine:3, got %v", err)
	}
	if err := a.Verify("docker.io/library/alpine:3"); err != nil {
		t.Errorf("explicit form should match, got %v", err)
	}
}

func TestVerify_DockerHubOwner(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "docker.io", Repository: "trefry/something", Tag: "v1"},
	})
	if err := a.Verify("trefry/something:v1"); err != nil {
		t.Errorf("docker.io owner shorthand should match, got %v", err)
	}
}

func TestVerify_RejectsAttackerImage(t *testing.T) {
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "trefry/dotnet-tester", Tag: "*"},
	})
	if err := a.Verify("attacker.example/evil/image:latest"); err == nil {
		t.Error("attacker registry must be rejected")
	}
	if err := a.Verify("docker.io/library/alpine:latest"); err == nil {
		t.Error("non-allowlisted docker.io image must be rejected")
	}
}

func TestSplitImage(t *testing.T) {
	cases := []struct {
		in       string
		registry string
		repo     string
		tag      string
	}{
		{"alpine", "docker.io", "library/alpine", "latest"},
		{"alpine:3", "docker.io", "library/alpine", "3"},
		{"trefry/cf:v1", "docker.io", "trefry/cf", "v1"},
		{"ghcr.io/trefry/cf:v1", "ghcr.io", "trefry/cf", "v1"},
		{"ghcr.io/trefry/cf", "ghcr.io", "trefry/cf", "latest"},
		{"localhost/foo:bar", "localhost", "foo", "bar"},
		{"registry:5000/foo:bar", "registry:5000", "foo", "bar"},
		{"registry:5000/foo", "registry:5000", "foo", "latest"},
		{"ghcr.io/trefry/cf@sha256:abc", "ghcr.io", "trefry/cf", "latest"},
	}
	for _, tc := range cases {
		t.Run(tc.in, func(t *testing.T) {
			r, repo, tag := splitImage(tc.in)
			if r != tc.registry || repo != tc.repo || tag != tc.tag {
				t.Errorf("got (%q,%q,%q) want (%q,%q,%q)", r, repo, tag, tc.registry, tc.repo, tc.tag)
			}
		})
	}
}
