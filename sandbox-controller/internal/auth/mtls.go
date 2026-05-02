// Package auth verifies that a presented client certificate matches the
// controller's configured allowlist. mTLS terminates in net/http; this package
// owns the post-handshake "is this cert one we'll accept?" check.
package auth

import (
	"crypto/x509"
	"errors"
	"fmt"
	"strings"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
)

// SubjectAllowlist verifies a cert's Subject against a configured set of
// SubjectPatterns. A cert matches if at least one pattern matches; a pattern
// matches if every non-empty field on the pattern equals the corresponding
// field on the cert's Subject (Organization is multi-valued; we match on first).
type SubjectAllowlist struct {
	patterns []config.SubjectPattern
}

// NewSubjectAllowlist compiles the allowlist; returns an error if no patterns
// are configured (mTLS without an allowlist is the same as no auth — refused).
func NewSubjectAllowlist(patterns []config.SubjectPattern) (*SubjectAllowlist, error) {
	if len(patterns) == 0 {
		return nil, errors.New("subject allowlist is empty")
	}
	for i, p := range patterns {
		if strings.TrimSpace(p.CommonName) == "" &&
			strings.TrimSpace(p.Organization) == "" &&
			strings.TrimSpace(p.OrganizationalUnit) == "" {
			return nil, fmt.Errorf("subject allowlist entry %d sets no fields", i)
		}
	}
	return &SubjectAllowlist{patterns: patterns}, nil
}

// Count returns the number of patterns. Used for startup logs.
func (a *SubjectAllowlist) Count() int { return len(a.patterns) }

// Verify checks that the cert is allowed. Returns nil when accepted, or a
// short error suitable for logging at info level. The cert itself MUST have
// already been verified against the trust chain by net/http's TLS layer.
func (a *SubjectAllowlist) Verify(cert *x509.Certificate) error {
	if cert == nil {
		return errors.New("no client certificate presented")
	}
	for _, p := range a.patterns {
		if patternMatches(p, cert) {
			return nil
		}
	}
	return fmt.Errorf("client subject %q not on allowlist", cert.Subject.String())
}

func patternMatches(p config.SubjectPattern, cert *x509.Certificate) bool {
	if cn := strings.TrimSpace(p.CommonName); cn != "" && cert.Subject.CommonName != cn {
		return false
	}
	if org := strings.TrimSpace(p.Organization); org != "" && !contains(cert.Subject.Organization, org) {
		return false
	}
	if ou := strings.TrimSpace(p.OrganizationalUnit); ou != "" && !contains(cert.Subject.OrganizationalUnit, ou) {
		return false
	}
	return true
}

func contains(haystack []string, needle string) bool {
	for _, s := range haystack {
		if s == needle {
			return true
		}
	}
	return false
}
