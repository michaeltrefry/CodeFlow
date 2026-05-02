// Package apilog wraps slog with the controller's structured-logging conventions.
//
// Every API request logs caller cert subject, route, status, duration, and
// (on errors) a stable error code rather than any raw user input — see
// docs/sandbox-executor.md §10.4 for the rationale.
package apilog

import (
	"log/slog"
	"os"
)

// New returns a JSON slog logger that writes to stderr at the given level.
// Stderr is the conventional sink for a server's structured logs because stdout
// is reserved for any future per-job stream relay.
func New(level slog.Level) *slog.Logger {
	handler := slog.NewJSONHandler(os.Stderr, &slog.HandlerOptions{
		Level:     level,
		AddSource: false,
	})
	return slog.New(handler)
}
