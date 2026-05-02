// Package telemetry sets up OpenTelemetry tracing and metrics for the
// sandbox-controller (sc-534).
//
// Per docs/sandbox-executor.md §14:
//   - W3C traceparent header is extracted on inbound requests so CodeFlow's
//     tracecontext propagates end-to-end (CodeFlow runner → controller →
//     child spans).
//   - Span structure under controller.run: validate, pull, spawn, wait,
//     teardown, artifact_collect.
//   - Metrics: jobs_started/succeeded/failed/timed_out/cancelled,
//     validator rejections by rule, spawn/run duration histograms, in-flight
//     jobs gauge.
//   - What does NOT go into telemetry: workspace contents, image tags from
//     rejected requests, stdout/stderr, full cert subjects (only coarse
//     "client identity" labels). See §14.3.
package telemetry

import (
	"context"
	"errors"
	"fmt"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/propagation"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.26.0"
	"go.opentelemetry.io/otel/trace"
)

// Config controls the OTel setup. When OTLPEndpoint is empty, telemetry is a
// no-op (returns no-op tracer/meter, exports nothing) — keeps local dev simple.
type Config struct {
	// OTLPEndpoint is the collector address, e.g. "http://otel-collector:4318".
	// Empty disables export.
	OTLPEndpoint string

	// ServiceName surfaces as service.name in resource attributes. Default
	// "sandbox-controller".
	ServiceName string

	// ServiceVersion surfaces as service.version. Set from main's commit hash.
	ServiceVersion string
}

// Telemetry holds the resolved tracer + meter + a shutdown func.
type Telemetry struct {
	Tracer   trace.Tracer
	Meter    metric.Meter
	Metrics  *Metrics
	shutdown func(context.Context) error
}

// Shutdown flushes pending exports and closes the providers.
func (t *Telemetry) Shutdown(ctx context.Context) error {
	if t == nil || t.shutdown == nil {
		return nil
	}
	return t.shutdown(ctx)
}

// Metrics collects the controller's named instruments. Constructed once and
// passed everywhere instrumentation is needed (handler, runner).
type Metrics struct {
	JobsStarted          metric.Int64Counter
	JobsSucceeded        metric.Int64Counter
	JobsFailed           metric.Int64Counter
	JobsTimedOut         metric.Int64Counter
	JobsCancelled        metric.Int64Counter
	ValidatorRejections  metric.Int64Counter
	RunDurationMs        metric.Float64Histogram
	SpawnDurationMs      metric.Float64Histogram
	InFlightJobs         metric.Int64UpDownCounter
}

// Setup installs global tracer + meter providers configured against the OTLP
// endpoint. When cfg.OTLPEndpoint is empty, returns a Telemetry with no-op
// instruments — the rest of the code path doesn't need to branch on
// "telemetry configured?".
func Setup(ctx context.Context, cfg Config) (*Telemetry, error) {
	if cfg.ServiceName == "" {
		cfg.ServiceName = "sandbox-controller"
	}

	if cfg.OTLPEndpoint == "" {
		// No-op path. Use the global no-op providers (the otel package's
		// default TracerProvider and MeterProvider are no-ops).
		tracer := otel.Tracer("sandbox-controller")
		meter := otel.Meter("sandbox-controller")
		m, err := buildMetrics(meter)
		if err != nil {
			return nil, fmt.Errorf("init no-op metrics: %w", err)
		}
		return &Telemetry{Tracer: tracer, Meter: meter, Metrics: m}, nil
	}

	res, err := resource.Merge(
		resource.Default(),
		resource.NewWithAttributes(
			semconv.SchemaURL,
			semconv.ServiceName(cfg.ServiceName),
			semconv.ServiceVersion(cfg.ServiceVersion),
		),
	)
	if err != nil {
		return nil, fmt.Errorf("init resource: %w", err)
	}

	// Trace exporter — HTTP/protobuf to keep dep size smaller than gRPC.
	traceExporter, err := otlptracehttp.New(ctx, otlptracehttp.WithEndpointURL(cfg.OTLPEndpoint+"/v1/traces"))
	if err != nil {
		return nil, fmt.Errorf("init trace exporter: %w", err)
	}
	tracerProvider := sdktrace.NewTracerProvider(
		sdktrace.WithBatcher(traceExporter, sdktrace.WithBatchTimeout(5*time.Second)),
		sdktrace.WithResource(res),
	)

	// Metric exporter — HTTP/protobuf.
	metricExporter, err := otlpmetrichttp.New(ctx, otlpmetrichttp.WithEndpointURL(cfg.OTLPEndpoint+"/v1/metrics"))
	if err != nil {
		// Best-effort cleanup on partial init.
		_ = tracerProvider.Shutdown(context.Background())
		return nil, fmt.Errorf("init metric exporter: %w", err)
	}
	meterProvider := sdkmetric.NewMeterProvider(
		sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metricExporter, sdkmetric.WithInterval(30*time.Second))),
		sdkmetric.WithResource(res),
	)

	otel.SetTracerProvider(tracerProvider)
	otel.SetMeterProvider(meterProvider)
	// W3C tracecontext + baggage; matches what CodeFlow's HttpClient emits.
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(
		propagation.TraceContext{},
		propagation.Baggage{},
	))

	tracer := tracerProvider.Tracer("sandbox-controller")
	meter := meterProvider.Meter("sandbox-controller")
	m, err := buildMetrics(meter)
	if err != nil {
		return nil, fmt.Errorf("init metrics: %w", err)
	}

	return &Telemetry{
		Tracer:  tracer,
		Meter:   meter,
		Metrics: m,
		shutdown: func(ctx context.Context) error {
			return errors.Join(
				tracerProvider.Shutdown(ctx),
				meterProvider.Shutdown(ctx),
			)
		},
	}, nil
}

func buildMetrics(meter metric.Meter) (*Metrics, error) {
	jobsStarted, err := meter.Int64Counter("cfsc.jobs.started",
		metric.WithDescription("Total /run requests that passed validation and reached the runner."))
	if err != nil {
		return nil, err
	}
	jobsSucceeded, err := meter.Int64Counter("cfsc.jobs.succeeded",
		metric.WithDescription("Jobs that completed with exit code == 0."))
	if err != nil {
		return nil, err
	}
	jobsFailed, err := meter.Int64Counter("cfsc.jobs.failed",
		metric.WithDescription("Jobs that completed with non-zero exit (or that errored before exit)."))
	if err != nil {
		return nil, err
	}
	jobsTimedOut, err := meter.Int64Counter("cfsc.jobs.timed_out",
		metric.WithDescription("Jobs the controller killed because they exceeded the per-job timeout."))
	if err != nil {
		return nil, err
	}
	jobsCancelled, err := meter.Int64Counter("cfsc.jobs.cancelled",
		metric.WithDescription("Jobs cancelled mid-flight via /cancel or context cancellation."))
	if err != nil {
		return nil, err
	}
	rejections, err := meter.Int64Counter("cfsc.validator.rejections",
		metric.WithDescription("/run validator rejections by rule (image_not_allowed, workspace_invalid, request_invalid, …)."))
	if err != nil {
		return nil, err
	}
	runDuration, err := meter.Float64Histogram("cfsc.run.duration_ms",
		metric.WithDescription("End-to-end /run duration in milliseconds (validator entry to response written)."),
		metric.WithUnit("ms"))
	if err != nil {
		return nil, err
	}
	spawnDuration, err := meter.Float64Histogram("cfsc.container.spawn_duration_ms",
		metric.WithDescription("Time from create to start across the runner's docker call sequence."),
		metric.WithUnit("ms"))
	if err != nil {
		return nil, err
	}
	inFlight, err := meter.Int64UpDownCounter("cfsc.jobs.in_flight",
		metric.WithDescription("Currently running jobs (incremented on /run entry, decremented on response)."))
	if err != nil {
		return nil, err
	}
	return &Metrics{
		JobsStarted:         jobsStarted,
		JobsSucceeded:       jobsSucceeded,
		JobsFailed:          jobsFailed,
		JobsTimedOut:        jobsTimedOut,
		JobsCancelled:       jobsCancelled,
		ValidatorRejections: rejections,
		RunDurationMs:       runDuration,
		SpawnDurationMs:     spawnDuration,
		InFlightJobs:        inFlight,
	}, nil
}
