// Package whitelist enforces the controller's default-deny image policy.
//
// Layer-2 validation per docs/sandbox-executor.md §10.2: the controller's
// own list of allowed images, independent of whatever CodeFlow's
// ContainerToolOptions said upstream. A request whose image isn't on this
// list is rejected with image_not_allowed before any docker call is made.
//
// Rule shape (intentionally narrow, no regex):
//
//	{ registry: "ghcr.io", repository: "trefry/dotnet-tester", tag: "10.0-sdk-*" }
//
// Tag patterns support exact match and a single trailing wildcard ("*").
// Regex was deliberately not chosen — auditable, plain-English-readable
// policy was the priority.
package whitelist

import (
	"errors"
	"fmt"
	"strings"
)

// Rule is one allowlist entry. CommitToml-shaped (sc-530's [[images.allowed]]
// blocks), but also constructible directly for tests.
type Rule struct {
	// Registry is the image's registry hostname, e.g. "ghcr.io" or "docker.io".
	// "" matches docker.io implicitly only when the request's image reference
	// is a bare name like "alpine:3" (legacy docker behaviour). Production
	// rules should always be explicit.
	Registry string `toml:"registry"`

	// Repository is the rest of the image path, e.g. "trefry/dotnet-tester"
	// or "library/alpine". Matches exactly.
	Repository string `toml:"repository"`

	// Tag is the tag pattern. Exact string match unless it ends with "*", in
	// which case the prefix must match. The wildcard cannot appear elsewhere
	// in the string (no internal "*", no "?"). Empty tag is treated as
	// "latest" to mirror docker's pull behaviour.
	Tag string `toml:"tag"`
}

// Allowlist holds the compiled rules.
type Allowlist struct {
	rules []Rule
}

// Compile validates and returns an Allowlist. An empty rule list means
// default-deny — every Verify call returns image_not_allowed. That's the
// intended posture; the operator opts into specific images via config.
func Compile(rules []Rule) (*Allowlist, error) {
	a := &Allowlist{}
	for i, r := range rules {
		r.Registry = strings.TrimSpace(r.Registry)
		r.Repository = strings.TrimSpace(r.Repository)
		r.Tag = strings.TrimSpace(r.Tag)

		if r.Repository == "" {
			return nil, fmt.Errorf("images.allowed[%d]: repository is required", i)
		}
		if strings.Count(r.Tag, "*") > 1 {
			return nil, fmt.Errorf("images.allowed[%d]: tag may contain at most one '*'", i)
		}
		if star := strings.Index(r.Tag, "*"); star >= 0 && star != len(r.Tag)-1 {
			return nil, fmt.Errorf("images.allowed[%d]: '*' is only allowed as a trailing wildcard", i)
		}
		// Catch-all `*` means any tag — explicit, auditable.
		a.rules = append(a.rules, r)
	}
	return a, nil
}

// Count returns the number of rules. Used in startup logs so operators can
// confirm the config they expected actually loaded.
func (a *Allowlist) Count() int {
	if a == nil {
		return 0
	}
	return len(a.rules)
}

// ErrImageNotAllowed is returned for default-deny rejections.
var ErrImageNotAllowed = errors.New("image_not_allowed")

// Verify returns nil if the image reference matches at least one rule, else
// ErrImageNotAllowed wrapped with the offending image. Callers should NOT
// surface the raw image string in operator-facing logs (potential intent
// leak) but the error wrapping is fine for caller-side handling.
func (a *Allowlist) Verify(image string) error {
	if a == nil || len(a.rules) == 0 {
		return fmt.Errorf("%w: %q (allowlist empty)", ErrImageNotAllowed, image)
	}
	registry, repo, tag := splitImage(image)
	for _, r := range a.rules {
		if matchesRule(r, registry, repo, tag) {
			return nil
		}
	}
	return fmt.Errorf("%w: %q", ErrImageNotAllowed, image)
}

// matchesRule applies one rule to a parsed image reference.
func matchesRule(r Rule, registry, repo, tag string) bool {
	// Registry: exact match. Empty rule registry means "match if the request's
	// registry is docker.io (the legacy default)".
	switch {
	case r.Registry == "":
		if registry != "docker.io" {
			return false
		}
	case r.Registry != registry:
		return false
	}
	if r.Repository != repo {
		return false
	}
	return matchTag(r.Tag, tag)
}

// matchTag implements the tag-pattern semantics: empty rule tag matches
// "latest" only; trailing "*" is a prefix match; otherwise exact.
func matchTag(rule, actual string) bool {
	if rule == "" {
		return actual == "latest"
	}
	if rule == "*" {
		return true
	}
	if strings.HasSuffix(rule, "*") {
		prefix := strings.TrimSuffix(rule, "*")
		return strings.HasPrefix(actual, prefix)
	}
	return rule == actual
}

// splitImage parses an image reference into (registry, repository, tag).
// Mirrors the docker reference grammar (simplified):
//
//	registry/repository:tag    -> ("registry", "repository", "tag")
//	registry/owner/name:tag    -> ("registry", "owner/name", "tag")
//	owner/name                 -> ("docker.io", "owner/name", "latest")
//	name                       -> ("docker.io", "library/name", "latest")
//
// Registry detection: the first slash-separated component is treated as a
// registry if it contains "." or ":" (docker's reference parser convention).
// Otherwise it's an owner under docker.io.
func splitImage(ref string) (registry, repository, tag string) {
	tag = "latest"

	// Split off tag.
	if at := strings.LastIndex(ref, "@"); at >= 0 {
		// Digest — strip; treat as "latest" for tag-pattern matching since
		// we never write digest-pinned policies in v1.
		ref = ref[:at]
	}
	if colon := strings.LastIndex(ref, ":"); colon > 0 {
		// Heuristic: if there's a slash after the last colon, it's a port,
		// not a tag.
		afterColon := ref[colon+1:]
		if !strings.Contains(afterColon, "/") {
			tag = afterColon
			ref = ref[:colon]
		}
	}

	// Split registry from repository.
	parts := strings.SplitN(ref, "/", 2)
	if len(parts) == 1 {
		// Single-component name like "alpine" — Docker Hub library.
		return "docker.io", "library/" + parts[0], tag
	}
	first, rest := parts[0], parts[1]
	if strings.ContainsAny(first, ".:") || first == "localhost" {
		// First component is a hostname.
		return first, rest, tag
	}
	// First component is an owner under Docker Hub.
	return "docker.io", ref, tag
}
