package auth

import (
	"crypto/x509"
	"crypto/x509/pkix"
	"testing"

	"github.com/michaeltrefry/codeflow/sandbox-controller/internal/config"
)

func certWith(cn string, orgs []string, ous []string) *x509.Certificate {
	return &x509.Certificate{
		Subject: pkix.Name{
			CommonName:         cn,
			Organization:       orgs,
			OrganizationalUnit: ous,
		},
	}
}

func TestNewSubjectAllowlist_RejectsEmpty(t *testing.T) {
	if _, err := NewSubjectAllowlist(nil); err == nil {
		t.Fatal("expected error for empty allowlist")
	}
}

func TestNewSubjectAllowlist_RejectsBlankEntry(t *testing.T) {
	_, err := NewSubjectAllowlist([]config.SubjectPattern{{}})
	if err == nil {
		t.Fatal("expected error for entry with no set fields")
	}
}

func TestSubjectAllowlist_Verify(t *testing.T) {
	patterns := []config.SubjectPattern{
		{CommonName: "codeflow-api", Organization: "trefry"},
		{CommonName: "codeflow-worker", Organization: "trefry"},
	}
	a, err := NewSubjectAllowlist(patterns)
	if err != nil {
		t.Fatalf("compile: %v", err)
	}

	cases := []struct {
		name    string
		cert    *x509.Certificate
		wantErr bool
	}{
		{
			name: "matching api cert is accepted",
			cert: certWith("codeflow-api", []string{"trefry"}, nil),
		},
		{
			name: "matching worker cert is accepted",
			cert: certWith("codeflow-worker", []string{"trefry"}, nil),
		},
		{
			name:    "wrong CN is rejected",
			cert:    certWith("codeflow-ui", []string{"trefry"}, nil),
			wantErr: true,
		},
		{
			name:    "right CN but wrong O is rejected",
			cert:    certWith("codeflow-api", []string{"attacker"}, nil),
			wantErr: true,
		},
		{
			name:    "missing O is rejected when pattern requires it",
			cert:    certWith("codeflow-api", nil, nil),
			wantErr: true,
		},
		{
			name:    "nil cert is rejected",
			cert:    nil,
			wantErr: true,
		},
		{
			name: "additional Organization values do not break match (first matches)",
			cert: certWith("codeflow-api", []string{"trefry", "other"}, nil),
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := a.Verify(tc.cert)
			if tc.wantErr && err == nil {
				t.Fatal("expected error, got nil")
			}
			if !tc.wantErr && err != nil {
				t.Fatalf("expected nil, got %v", err)
			}
		})
	}
}

func TestSubjectAllowlist_OUMatch(t *testing.T) {
	a, err := NewSubjectAllowlist([]config.SubjectPattern{
		{CommonName: "codeflow-api", OrganizationalUnit: "platform"},
	})
	if err != nil {
		t.Fatalf("compile: %v", err)
	}

	if err := a.Verify(certWith("codeflow-api", nil, []string{"platform"})); err != nil {
		t.Fatalf("expected accept, got %v", err)
	}
	if err := a.Verify(certWith("codeflow-api", nil, []string{"other"})); err == nil {
		t.Fatal("expected reject for wrong OU")
	}
}
