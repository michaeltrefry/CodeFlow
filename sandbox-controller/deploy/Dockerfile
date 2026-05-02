# syntax=docker/dockerfile:1.7
#
# Sandbox controller image. sc-528 builds the static binary on a distroless base.
# sc-538 layers AppArmor + seccomp profiles, read-only FS, cap_drop, and the rest
# of the deploy hardening on top of this image at runtime via compose flags.
#
# Image size target: < 30 MB.

FROM golang:1.26-alpine AS build
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY . .

ARG COMMIT=dev
ARG BUILD_TIME=unknown
RUN CGO_ENABLED=0 GOOS=linux go build \
      -trimpath \
      -ldflags="-s -w -X main.commit=${COMMIT} -X main.buildTime=${BUILD_TIME}" \
      -o /out/sandbox-controller \
      ./cmd/sandbox-controller

# Distroless `nonroot` image runs as uid 65532. Static base — no glibc, no shell,
# no package manager. The only thing inside the runtime image is our binary and
# the CA certs needed for outbound TLS (image pulls happen at dockerd, not here,
# so this is just defensive — our binary makes no outbound HTTPS calls).
FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/sandbox-controller /usr/local/bin/sandbox-controller

USER nonroot:nonroot
ENTRYPOINT ["/usr/local/bin/sandbox-controller"]
CMD ["-config", "/etc/cfsc/config.toml"]
