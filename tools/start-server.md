# Starting from source

[中文](start-server.zh.md)

Follow [quick start](../docs/quickstart.md) for a local isolated instance. Every local Host invocation must include `--patch tools/local-checks/isolated-memory.patch.yaml`.

The launch scripts load repository-local configuration and may use persistent services. Inspect their arguments and `.env` before using them for an intended deployment. Build and deployment-update scripts preserve prior outputs for rollback; never replace a running deployment or remove backups as part of a local check. See [deployment](../docs/deployment.md).
