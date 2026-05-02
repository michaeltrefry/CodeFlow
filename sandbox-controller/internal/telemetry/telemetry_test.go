package telemetry

import (
	"context"
	"testing"
)

func TestSetup_NoOpWhenEndpointEmpty(t *testing.T) {
	tel, err := Setup(context.Background(), Config{})
	if err != nil {
		t.Fatalf("Setup(empty): %v", err)
	}
	if tel.Tracer == nil || tel.Meter == nil {
		t.Fatal("no-op Setup must still produce non-nil tracer/meter so callers don't have to branch")
	}
	if tel.Metrics == nil {
		t.Fatal("Metrics must be populated")
	}
	// Shutdown on a no-op Telemetry must succeed.
	if err := tel.Shutdown(context.Background()); err != nil {
		t.Errorf("Shutdown on no-op: %v", err)
	}
}

func TestShutdown_OnNilTelemetryIsSafe(t *testing.T) {
	var tel *Telemetry
	if err := tel.Shutdown(context.Background()); err != nil {
		t.Errorf("Shutdown on nil: %v", err)
	}
}

func TestMetrics_RecordsToNoOp(t *testing.T) {
	tel, err := Setup(context.Background(), Config{})
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	// All instruments should accept calls without error.
	tel.Metrics.JobsStarted.Add(ctx, 1)
	tel.Metrics.JobsSucceeded.Add(ctx, 1)
	tel.Metrics.JobsFailed.Add(ctx, 1)
	tel.Metrics.JobsTimedOut.Add(ctx, 1)
	tel.Metrics.JobsCancelled.Add(ctx, 1)
	tel.Metrics.ValidatorRejections.Add(ctx, 1)
	tel.Metrics.RunDurationMs.Record(ctx, 42.0)
	tel.Metrics.SpawnDurationMs.Record(ctx, 7.5)
	tel.Metrics.InFlightJobs.Add(ctx, 1)
	tel.Metrics.InFlightJobs.Add(ctx, -1)
}
