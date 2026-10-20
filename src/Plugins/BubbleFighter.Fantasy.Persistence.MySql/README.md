# MySQL persistence

[中文](README.zh.md)

The `persistence.mysql` row provides repository interfaces using MySQL. Configure it through the profile and environment variables listed in the repository's `.env.example`; the database name defaults to `bf_game_db` and may be any existing database. A nonempty `BFF_MYSQL_PASSWORD` is required for interpolation.

Connection verification and `auto_migrate` control startup behavior. Read the row's options before enabling either. Back up an existing database before applying migrations. Embedded migration scripts are ordered, versioned inputs: never edit an already applied migration to change a deployed schema.

Use the isolated memory patch for local Host checks. A successful build or memory-backed test does not validate a live database. See the [deployment guide](../../../docs/deployment.md).
