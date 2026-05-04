# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY CodeFlow.slnx ./

COPY CodeFlow.Contracts/CodeFlow.Contracts.csproj CodeFlow.Contracts/
COPY CodeFlow.Runtime/CodeFlow.Runtime.csproj CodeFlow.Runtime/
COPY CodeFlow.Persistence/CodeFlow.Persistence.csproj CodeFlow.Persistence/
COPY CodeFlow.Orchestration/CodeFlow.Orchestration.csproj CodeFlow.Orchestration/
COPY CodeFlow.Host/CodeFlow.Host.csproj CodeFlow.Host/
COPY CodeFlow.Worker/CodeFlow.Worker.csproj CodeFlow.Worker/

RUN dotnet restore CodeFlow.Worker/CodeFlow.Worker.csproj

COPY CodeFlow.Contracts/ CodeFlow.Contracts/
COPY CodeFlow.Runtime/ CodeFlow.Runtime/
COPY CodeFlow.Persistence/ CodeFlow.Persistence/
COPY CodeFlow.Orchestration/ CodeFlow.Orchestration/
COPY CodeFlow.Host/ CodeFlow.Host/
COPY CodeFlow.Worker/ CodeFlow.Worker/

RUN dotnet publish CodeFlow.Worker/CodeFlow.Worker.csproj \
      -c Release \
      --no-restore \
      -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Install git so vcs.clone (via IGitCli) and agent-issued `run_command git ...` both work.
# openssh-client covers SSH remotes; ca-certificates is usually pulled by base but is cheap to
# pin explicitly. See sc-659 / epic 658 for the per-trace credential-helper plan that complements
# this — agents never see a token; auth flows through git-credential-codeflow at runtime.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates openssh-client \
 && rm -rf /var/lib/apt/lists/*

# Run as a non-root uid so post-exploitation does not execute as root. We use the .NET image's
# standard app uid rather than assuming user-management tools like adduser are installed.
ARG APP_UID=1654
# Pre-create /workspace so a missing bind-mount doesn't leave it as root:root and break host tools
# that operate against {WorkingDirectoryRoot}/{traceId}/. See CodeFlow.Api/Dockerfile for the
# matching commentary.
RUN mkdir -p /app/artifacts /workspace && \
    chown -R ${APP_UID}:${APP_UID} /app /workspace

COPY --from=build --chown=${APP_UID}:${APP_UID} /app/publish ./
USER ${APP_UID}

ENTRYPOINT ["dotnet", "CodeFlow.Worker.dll"]
