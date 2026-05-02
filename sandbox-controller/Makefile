.PHONY: build test vet lint dev-certs run image clean

GO        ?= go
BIN       ?= bin/sandbox-controller
COMMIT    := $(shell git -C $(CURDIR) rev-parse --short HEAD 2>/dev/null || echo dev)
BUILDTIME := $(shell date -u +%Y-%m-%dT%H:%M:%SZ)

build:
	CGO_ENABLED=0 $(GO) build -trimpath \
	  -ldflags="-s -w -X main.commit=$(COMMIT) -X main.buildTime=$(BUILDTIME)" \
	  -o $(BIN) ./cmd/sandbox-controller

test:
	$(GO) test ./...

vet:
	$(GO) vet ./...

lint: vet

dev-certs:
	./scripts/gen-dev-certs.sh

run: build
	$(BIN) -config deploy/controller-config.dev.toml

image:
	docker build \
	  --build-arg COMMIT=$(COMMIT) \
	  --build-arg BUILD_TIME=$(BUILDTIME) \
	  -f deploy/Dockerfile \
	  -t codeflow-sandbox-controller:$(COMMIT) .

clean:
	rm -rf bin/ deploy/dev-tls/
