# Local Integration Stack

CodeFlow uses a shared Docker-based local integration stack for the persistence and bus phases of the kickoff plan.

The stack lives at the repo root in `docker-compose.yml` and provides:

- MariaDB for EF Core migrations, repositories, and outbox storage
- RabbitMQ with the management UI enabled for local messaging and saga validation

## Start The Stack

From the repository root:

```bash
docker compose up -d
```

To stop the services:

```bash
docker compose down
```

To stop the services and remove persisted local volumes:

```bash
docker compose down -v
```

## Services

### MariaDB

- Image: `mariadb:11.4`
- Host port: `3306`
- Database: `codeflow`
- Username: `codeflow`
- Password: `codeflow_dev`
- Root password: `codeflow_root`

Connection settings for later cards:

- Host: `127.0.0.1`
- Port: `3306`
- Database: `codeflow`
- Username: `codeflow`
- Password: `codeflow_dev`

Example ADO.NET / EF Core connection string:

```text
Server=127.0.0.1;Port=3306;Database=codeflow;User=codeflow;Password=codeflow_dev;
```

The service uses a named Docker volume so local schema and data survive container restarts during development.

### RabbitMQ

- Image: `rabbitmq:4.0-management`
- AMQP port: `5673`
- Management UI: `http://127.0.0.1:15673`
- Username: `codeflow`
- Password: `codeflow_dev`
- Virtual host: `codeflow`

Connection settings for later cards:

- Host: `127.0.0.1`
- Port: `5673`
- Username: `codeflow`
- Password: `codeflow_dev`
- Virtual host: `codeflow`

The host ports intentionally use CodeFlow-specific defaults instead of RabbitMQ's common local defaults so the stack is less likely to collide with an already-running broker on a development machine.

The management image enables the management UI only. No delayed-exchange or other optional plugins are added in this kickoff stack, because the current Phase 2 and Phase 3 work does not require them.

## Health Checks

Both services include Compose health checks so later integration-oriented cards can depend on stable startup behavior:

- MariaDB uses `mariadb-admin ping`
- RabbitMQ uses `rabbitmq-diagnostics -q ping`

Use `docker compose ps` to confirm both containers are healthy after startup.

## Intended Consumers

This stack is the shared baseline for:

- `[2.1]` EF Core + MariaDB setup
- `[3.2]` MassTransit + RabbitMQ + EF outbox wiring
- later bus, saga, and persistence integration tests that need realistic local infrastructure

Future cards should reuse these defaults unless a later architectural decision explicitly changes them.
