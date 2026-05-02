// Package config loads the controller's TOML configuration.
package config

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"log/slog"
	"os"
	"strings"

	"github.com/BurntSushi/toml"
)

// Config is the top-level controller configuration.
type Config struct {
	Server    ServerConfig    `toml:"server"`
	TLS       TLSConfig       `toml:"tls"`
	Logging   LoggingConfig   `toml:"logging"`
	Runner    RunnerConfig    `toml:"runner"`
	Images    ImagesConfig    `toml:"images"`
	Workspace WorkspaceConfig `toml:"workspace"`
	Sweeper   SweeperConfig   `toml:"sweeper"`
	Telemetry TelemetryConfig `toml:"telemetry"`

	rawHash string
}

// SweeperConfig governs sc-533's periodic cleanup. Zero values fall back to
// the sweeper package defaults (30m interval, 24h container TTL, 7d results
// TTL).
type SweeperConfig struct {
	IntervalSeconds     int `toml:"interval_seconds"`
	ContainerTTLSeconds int `toml:"container_ttl_seconds"`
	ResultsTTLSeconds   int `toml:"results_ttl_seconds"`
}

// TelemetryConfig configures sc-534's OpenTelemetry exporter. When OTLPEndpoint
// is empty, all instruments become no-ops and nothing is exported — keeps dev
// running without an external collector.
type TelemetryConfig struct {
	OTLPEndpoint   string `toml:"otlp_endpoint"`
	ServiceName    string `toml:"service_name"`
	ServiceVersion string `toml:"service_version"`
}

// WorkspaceConfig governs sc-531's workspace path validation. WorkdirRoot must
// be an absolute, existing directory at startup. ResultsDirName defaults to
// ".results" if empty.
type WorkspaceConfig struct {
	WorkdirRoot    string `toml:"workdir_root"`
	ResultsDirName string `toml:"results_dir_name"`
}

// ImagesConfig is the controller's default-deny image policy (sc-530). An
// empty Allowed list is intentionally allowed at config-load time but every
// /run will be rejected with image_not_allowed — operators opt in to specific
// images explicitly.
type ImagesConfig struct {
	Allowed []ImageRule `toml:"allowed"`
}

// ImageRule is one entry in the allowlist. Mirrors whitelist.Rule; we don't
// import the whitelist package here to avoid a cycle (whitelist depends on
// config types only by structural equivalence — the controller adapts at
// startup time).
type ImageRule struct {
	Registry   string `toml:"registry"`
	Repository string `toml:"repository"`
	Tag        string `toml:"tag"`
}

// RunnerConfig knobs for the runsc-backed job runner (sc-529).
type RunnerConfig struct {
	// DockerSocketPath is the unix socket the controller dials. Defaults to
	// /var/run/docker.sock when empty.
	DockerSocketPath string `toml:"docker_socket_path"`

	// ImagePullTimeoutSeconds caps how long an image pull may take. Separate
	// budget from the per-job timeout so a slow registry doesn't eat into it.
	ImagePullTimeoutSeconds int `toml:"image_pull_timeout_seconds"`

	// ContainerTeardownTimeoutSeconds caps best-effort kill+remove on tear-down.
	ContainerTeardownTimeoutSeconds int `toml:"container_teardown_timeout_seconds"`
}

// ServerConfig controls the HTTP listener.
type ServerConfig struct {
	// Listen is a host:port to bind. mTLS-only; there is no plaintext listener.
	Listen string `toml:"listen"`
}

// TLSConfig points at the server cert/key, the client CA bundle, and the
// allowlist of client cert subjects (api, worker, etc.).
type TLSConfig struct {
	CertPath              string           `toml:"cert_path"`
	KeyPath               string           `toml:"key_path"`
	ClientCAPath          string           `toml:"client_ca_path"`
	AllowedClientSubjects []SubjectPattern `toml:"allowed_client_subjects"`
}

// SubjectPattern is a per-component match against an X.509 subject DN.
// At least one of CommonName / Organization / OrganizationalUnit must be set;
// every set field must match the cert's Subject for the cert to be accepted.
type SubjectPattern struct {
	CommonName         string `toml:"common_name"`
	Organization       string `toml:"organization"`
	OrganizationalUnit string `toml:"organizational_unit"`
}

// LoggingConfig configures slog.
type LoggingConfig struct {
	Level string `toml:"level"`
}

// SlogLevel returns the slog.Level for the configured string.
func (l LoggingConfig) SlogLevel() slog.Level {
	switch strings.ToLower(strings.TrimSpace(l.Level)) {
	case "debug":
		return slog.LevelDebug
	case "warn", "warning":
		return slog.LevelWarn
	case "error":
		return slog.LevelError
	default:
		return slog.LevelInfo
	}
}

// Load reads and validates the TOML config at the given path.
func Load(path string) (*Config, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read %q: %w", path, err)
	}

	var cfg Config
	if _, err := toml.Decode(string(raw), &cfg); err != nil {
		return nil, fmt.Errorf("parse %q: %w", path, err)
	}

	if err := cfg.validate(); err != nil {
		return nil, err
	}

	sum := sha256.Sum256(raw)
	cfg.rawHash = hex.EncodeToString(sum[:])
	return &cfg, nil
}

// Hash returns a SHA-256 hex digest of the raw config file contents. Surfaced via
// /version so operators can confirm SIGHUP reloads picked up the new file.
func (c *Config) Hash() string {
	return c.rawHash
}

func (c *Config) validate() error {
	var errs []string

	if strings.TrimSpace(c.Server.Listen) == "" {
		errs = append(errs, "server.listen must be set (e.g. \"0.0.0.0:8443\")")
	}

	if strings.TrimSpace(c.TLS.CertPath) == "" {
		errs = append(errs, "tls.cert_path must be set")
	}
	if strings.TrimSpace(c.TLS.KeyPath) == "" {
		errs = append(errs, "tls.key_path must be set")
	}
	if strings.TrimSpace(c.TLS.ClientCAPath) == "" {
		errs = append(errs, "tls.client_ca_path must be set (mTLS is required)")
	}
	if len(c.TLS.AllowedClientSubjects) == 0 {
		errs = append(errs, "tls.allowed_client_subjects must contain at least one entry")
	}
	for i, p := range c.TLS.AllowedClientSubjects {
		if strings.TrimSpace(p.CommonName) == "" &&
			strings.TrimSpace(p.Organization) == "" &&
			strings.TrimSpace(p.OrganizationalUnit) == "" {
			errs = append(errs, fmt.Sprintf("tls.allowed_client_subjects[%d] must set at least one of common_name, organization, organizational_unit", i))
		}
	}

	if len(errs) > 0 {
		return fmt.Errorf("invalid config: %s", strings.Join(errs, "; "))
	}
	return nil
}
