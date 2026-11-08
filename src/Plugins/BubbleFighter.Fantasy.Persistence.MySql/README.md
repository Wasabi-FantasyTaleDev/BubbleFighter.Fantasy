# MySQL persistence

[中文](README.zh.md)

The `persistence.mysql` row provides repository interfaces using MySQL. Configure it through the profile and environment variables listed in the repository's `.env.example`; the database name defaults to `bf_game_db` and may be any existing database. A nonempty `BFF_MYSQL_PASSWORD` is required for interpolation.

Connection verification and `auto_migrate` control startup behavior. Read the row's options before enabling either. Back up an existing database before applying migrations. Embedded migration scripts are ordered, versioned inputs: never edit an already applied migration to change a deployed schema.

Use the isolated memory patch for local Host checks. A successful build or memory-backed test does not validate a live database. See the [deployment guide](../../../docs/deployment.md).

The optional `persistence.mysql-account-ownership` row provides `IAccountOwnershipStore`. It pins the database marker from migration 0035 and holds a MySQL named lock on a dedicated, unpooled connection for each account. Acquisition never waits for an existing owner; connection failures and exhausted capacity are errors. Verify the original hold with `IsHeldAsync`; a lost connection is never reconnected or reacquired by that handle. Release the handle only after draining the account's work. Unloading the row closes active holds and drains pending acquisitions.

`profiles/bundles/cluster-account-store.patch.yaml` shows the configuration. Supply the authority connection string and database marker through the environment, cap `max_owners` for the database's available memory, and protect remote database traffic. This row is an occupancy primitive: it does not change login handlers, fence repository mutations, replicate data or enable shared-node account admission. Standalone profiles do not mount it.
