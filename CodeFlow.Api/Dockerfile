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

# Run as a non-root user so post-exploitation (SSRF, deserialization, file upload) runs with
# uid 1000 instead of root. The artifacts mount point is pre-created with the right ownership
# so docker-compose's volume mount inherits it.
RUN adduser --disabled-password --gecos "" --uid 1000 codeflow && \
    mkdir -p /app/artifacts && \
    chown -R codeflow:codeflow /app

COPY --from=build --chown=codeflow:codeflow /app/publish ./
USER codeflow

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CodeFlow.Api.dll"]
