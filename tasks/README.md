# Simplified Cosmos Work Plan

These tasks replace the previous Cosmos implementation and migration plan. They are ordered to
keep the application buildable and SQL-backed at every intermediate commit.

1. [`0100_retire_existing_cosmos_provider.md`](0100_retire_existing_cosmos_provider.md)
2. [`0200_simplify_refresh_and_maintenance.md`](0200_simplify_refresh_and_maintenance.md)
3. [`0300_add_three_container_cosmos_foundation.md`](0300_add_three_container_cosmos_foundation.md)
4. [`0400_implement_cosmos_list_and_channel_repositories.md`](0400_implement_cosmos_list_and_channel_repositories.md)
5. [`0500_implement_share_links_and_enable_cosmos.md`](0500_implement_share_links_and_enable_cosmos.md)
6. [`0600_validate_emulator_and_azure_free_tier.md`](0600_validate_emulator_and_azure_free_tier.md)
7. [`0700_implement_offline_sql_to_cosmos_import.md`](0700_implement_offline_sql_to_cosmos_import.md)
8. [`0800_rehearse_migration_and_rollback.md`](0800_rehearse_migration_and_rollback.md)
9. [`0900_cut_over_test_server.md`](0900_cut_over_test_server.md)

Tasks 0100 and 0200 back out the current over-complex implementation. Tasks 0300 through 0600
build and prove the simplified provider. Tasks 0700 through 0900 migrate and cut over the existing
test server.

Follow [`PREAMBLE.md`](PREAMBLE.md) for status, scope, validation, and completion rules.
