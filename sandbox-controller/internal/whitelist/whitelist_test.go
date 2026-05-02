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

func TestCompile_RejectsMultipleStarsInRepo(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "*/foo/*", Tag: "1"}}); err == nil {
		t.Fatal("expected error for two '*' in repository")
	}
}

func TestCompile_RejectsLeadingStarInRepo(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "*/foo", Tag: "1"}}); err == nil {
		t.Fatal("expected error for non-trailing '*' in repository")
	}
}

func TestCompile_RejectsInternalStarInRepo(t *testing.T) {
	if _, err := Compile([]Rule{{Registry: "ghcr.io", Repository: "trefry/*/sub", Tag: "1"}}); err == nil {
		t.Fatal("expected error for internal '*' in repository")
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

func TestVerify_RepoTrailingWildcard(t *testing.T) {
	a := mustCompile(t, []Rule{
		// Trust all of Microsoft's .NET namespace.
		{Registry: "mcr.microsoft.com", Repository: "dotnet/*", Tag: "*"},
	})
	cases := []struct {
		image   string
		wantErr bool
	}{
		{"mcr.microsoft.com/dotnet/sdk:10.0", false},
		{"mcr.microsoft.com/dotnet/runtime:8.0-alpine", false},
		{"mcr.microsoft.com/dotnet/aspnet:9.0", false},
		// Different registry — rejected.
		{"docker.io/dotnet/sdk:10.0", true},
		// Same registry, different namespace — rejected.
		{"mcr.microsoft.com/azure-cli:latest", true},
		// Prefix matches but in unrelated namespace — rejected. ("dotnetcore"
		// would match the prefix string "dotnet" so we need the trailing
		// slash to bound the namespace properly. Operators should write
		// "dotnet/*" not "dotnet*" to avoid that.)
		{"mcr.microsoft.com/dotnetcore-imposter:1.0", true},
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

func TestVerify_RepoCatchAllStar(t *testing.T) {
	// "*" matches any repo within the configured registry. Operators use
	// this for fully-trusted private registries.
	a := mustCompile(t, []Rule{
		{Registry: "ghcr.io", Repository: "*", Tag: "*"},
	})
	if err := a.Verify("ghcr.io/trefry/dotnet-tester:10.0-sdk"); err != nil {
		t.Errorf("repo '*' should match anything in ghcr.io, got %v", err)
	}
	if err := a.Verify("ghcr.io/library/foo:latest"); err != nil {
		t.Errorf("repo '*' should match library/foo, got %v", err)
	}
	if err := a.Verify("docker.io/library/alpine:3"); err == nil {
		t.Errorf("repo '*' should NOT match a different registry")
	}
}

func TestVerify_RepoWildcardWithSpecificTag(t *testing.T) {
	// Repo wildcard composes with a pinned tag pattern.
	a := mustCompile(t, []Rule{
		{Registry: "docker.io", Repository: "library/*", Tag: "3"},
	})
	if err := a.Verify("alpine:3"); err != nil {
		t.Errorf("library/alpine:3 should match library/* + tag 3, got %v", err)
	}
	if err := a.Verify("alpine:latest"); err == nil {
		t.Errorf("alpine:latest should NOT match because tag is pinned to '3'")
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
