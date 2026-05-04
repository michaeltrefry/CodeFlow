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
COPY CodeFlow.Api/CodeFlow.Api.csproj CodeFlow.Api/

RUN dotnet restore CodeFlow.Api/CodeFlow.Api.csproj

COPY CodeFlow.Contracts/ CodeFlow.Contracts/
COPY CodeFlow.Runtime/ CodeFlow.Runtime/
COPY CodeFlow.Persistence/ CodeFlow.Persistence/
COPY CodeFlow.Orchestration/ CodeFlow.Orchestration/
COPY CodeFlow.Host/ CodeFlow.Host/
COPY CodeFlow.Api/ CodeFlow.Api/

RUN dotnet publish CodeFlow.Api/CodeFlow.Api.csproj \
      -c Release \
      --no-restore \
      -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install git so vcs.clone (via IGitCli) and agent-issued `run_command git ...` both work.
# openssh-client covers SSH remotes; ca-certificates is usually pulled by base but is cheap to
# pin explicitly. See sc-659 / epic 658 for the per-trace credential-helper plan that complements
# this — agents never see a token; auth flows through git-credential-codeflow at runtime.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates openssh-client \
 && rm -rf /var/lib/apt/lists/*

# Run as a non-root uid so post-exploitation (SSRF, deserialization, file upload) does not
# execute as root. We use the .NET image's standard app uid rather than assuming user-management
# tools like adduser are installed in the runtime image.
ARG APP_UID=1654
# Pre-create /workspace + /workspace/assistant with the app uid before the runtime bind-mounts.
# Docker creates a mount's parent as root:root if it doesn't already exist, which would otherwise
# prevent the assistant from creating its per-conversation workspace at /workspace/assistant.
# Pre-creating both subdirs in the image baseline keeps the assistant workspace writable when no
# bind-mount is configured for it.
RUN mkdir -p /app/artifacts /workspace/assistant /var/lib/codeflow/git-creds && \
    chown -R ${APP_UID}:${APP_UID} /app /workspace /var/lib/codeflow && \
    chmod 0700 /var/lib/codeflow/git-creds

COPY --from=build --chown=${APP_UID}:${APP_UID} /app/publish ./
USER ${APP_UID}

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CodeFlow.Api.dll"]
