-- 由 scripts/generate-entities.py 从 schema-dump.json 生成，请勿手工编辑。
-- 目标数据库：sqlite
-- 列类型取自 GORM 的 sqlite dialector DataTypeOf，
-- 保证与 Go 侧 AutoMigrate 产出的物理结构一致。

CREATE TABLE IF NOT EXISTS "admin_audit_events" (
    "id" text NOT NULL,
    "actor_user_id" text,
    "action" text,
    "target_type" text,
    "target_id" text,
    "summary" text,
    "metadata_json" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "agent_profiles" (
    "id" text NOT NULL,
    "user_id" text,
    "scope" text,
    "project_id" text,
    "canvas_id" text,
    "content" text,
    "revision" integer,
    "hash" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "announcement_image_drafts" (
    "resource_id" text NOT NULL,
    "user_id" text,
    "created_at" datetime,
    PRIMARY KEY ("resource_id")
);

CREATE TABLE IF NOT EXISTS "announcements" (
    "id" text NOT NULL,
    "title" text,
    "content" text,
    "image_resource_id" text,
    "level" text,
    "pinned" numeric,
    "status" text,
    "created_by" text,
    "published_at" datetime,
    "closed_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "api_call_logs" (
    "id" text NOT NULL,
    "user_id" text,
    "trace_id" text,
    "request_id" text,
    "channel_id" text,
    "task_id" text,
    "billing_order_id" text,
    "source" text,
    "capability" text,
    "operation" text,
    "request_kind" text,
    "billable" numeric,
    "api_format" text,
    "method" text,
    "path" text,
    "model" text,
    "status" text,
    "status_code" integer,
    "duration_ms" integer,
    "poll_count" integer,
    "provider_status" text,
    "input_tokens" integer,
    "output_tokens" integer,
    "cached_tokens" integer,
    "usage_available" numeric,
    "media_count" integer,
    "video_seconds" integer,
    "provider_request_id" text,
    "estimated_cost_micros" integer,
    "cost_available" numeric,
    "currency" text,
    "error_code" text,
    "error" text,
    "concurrency_limit" integer,
    "upstream_url" text,
    "request_content_type" text,
    "request_body" text,
    "response_body" text,
    "started_at" datetime,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "ark_private_asset_bindings" (
    "id" text NOT NULL,
    "user_id" text,
    "resource_id" text,
    "project_name" text,
    "asset_group_id" text,
    "ark_asset_id" text,
    "status" text,
    "error" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_folders" (
    "id" text NOT NULL,
    "user_id" text,
    "name" text,
    "name_key" text,
    "position" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_representations" (
    "id" text NOT NULL,
    "task_id" text,
    "asset_version_id" text,
    "resource_id" text,
    "media_type" text,
    "role" text,
    "metadata_json" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_versions" (
    "id" text NOT NULL,
    "asset_id" text,
    "version" integer,
    "status" text,
    "definition_json" text,
    "prompt" text,
    "note" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "assets" (
    "id" text NOT NULL,
    "user_id" text,
    "folder_id" text,
    "kind" text,
    "category" text,
    "status" text,
    "primary_version_id" text,
    "title" text,
    "payload_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "auth_sessions" (
    "id" text NOT NULL,
    "user_id" text,
    "token_hash" text,
    "expires_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "billing_orders" (
    "id" text NOT NULL,
    "user_id" text,
    "idempotency_key" text,
    "task_id" text,
    "channel_id" text,
    "channel_model_id" text,
    "price_tier_id" text,
    "price_tier_version" integer,
    "price_selector_json" text,
    "model" text,
    "capability" text,
    "scene" text,
    "billing_mode" text,
    "price_version" integer,
    "unit_price_microcredits" integer,
    "multiplier_basis_points" integer,
    "quantity" integer,
    "amount_microcredits" integer,
    "reserved_amount_microcredits" integer,
    "charge_limit_microcredits" integer,
    "actual_amount_microcredits" integer,
    "refunded_amount_microcredits" integer,
    "input_token_price_microcredits" integer,
    "output_token_price_microcredits" integer,
    "cached_token_price_microcredits" integer,
    "input_tokens" integer,
    "output_tokens" integer,
    "cached_tokens" integer,
    "usage_available" numeric,
    "status" text,
    "provider_request_id" text,
    "error" text,
    "resolved_by" text,
    "resolution_note" text,
    "started_at" datetime,
    "settled_at" datetime,
    "refunded_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_projects" (
    "id" text NOT NULL,
    "user_id" text,
    "project_id" text,
    "title" text,
    "payload_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_shares" (
    "id" text NOT NULL,
    "user_id" text,
    "project_id" text,
    "token_hash" text,
    "token_cipher" text,
    "enabled" numeric,
    "expires_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_unit_links" (
    "id" text NOT NULL,
    "project_id" text,
    "canvas_id" text,
    "unit_id" text,
    "role" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "channel_model_price_tiers" (
    "id" text NOT NULL,
    "channel_model_id" text,
    "selector_key" text NOT NULL DEFAULT '{}',
    "selector_json" text NOT NULL DEFAULT '{}',
    "resolution" text NOT NULL DEFAULT '*',
    "video_seconds" integer NOT NULL DEFAULT 0,
    "provider_model_key" text,
    "billing_mode" text,
    "unit_price_microcredits" integer,
    "input_token_price_microcredits" integer,
    "output_token_price_microcredits" integer,
    "cached_token_price_microcredits" integer,
    "price_configured" numeric,
    "enabled" numeric,
    "price_version" integer,
    "created_at" datetime,
    "updated_at" datetime,
    "deleted_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "channel_models" (
    "id" text NOT NULL,
    "channel_id" text,
    "model_key" text,
    "provider_model_key" text,
    "display_name" text,
    "sort_order" integer NOT NULL DEFAULT 0,
    "icon" text,
    "capability" text,
    "protocol" text,
    "billing_mode" text,
    "unit_price_microcredits" integer,
    "input_token_price_microcredits" integer,
    "output_token_price_microcredits" integer,
    "cached_token_price_microcredits" integer,
    "price_configured" numeric,
    "enabled" numeric,
    "price_version" integer,
    "capability_config_json" text,
    "capability_version" integer,
    "created_at" datetime,
    "updated_at" datetime,
    "deleted_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "character_voice_bindings" (
    "id" text NOT NULL,
    "asset_version_id" text,
    "voice_profile_id" text,
    "instructions" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "cloud_agent_canvas_mutations" (
    "id" text NOT NULL,
    "run_id" text,
    "user_id" text,
    "canvas_id" text,
    "step_id" text,
    "operation" text,
    "before_snapshot_hash" text,
    "after_snapshot_hash" text,
    "before_json" text,
    "has_submitted_task" numeric,
    "status" text,
    "created_at" datetime,
    "undone_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "cloud_agent_executions" (
    "id" text NOT NULL,
    "user_id" text,
    "status" text,
    "revision" integer,
    "canvas_id" text,
    "active_task_id" text,
    "media_task_id" text,
    "cleanup_pending" numeric NOT NULL DEFAULT false,
    "failure_message" text,
    "state_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "creation_runs" (
    "id" text NOT NULL,
    "user_id" text,
    "client_key" text,
    "create_hash" text,
    "canvas_id" text,
    "revision" integer,
    "execution_epoch" integer,
    "execution_owner" text,
    "lease_expires_at" datetime,
    "status" text,
    "state_json" text,
    "approved_proposal_version" integer,
    "approved_proposal_hash" text,
    "approved_operations_json" text,
    "approved_canvas_json" text,
    "approved_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "creation_submissions" (
    "id" text NOT NULL,
    "user_id" text,
    "run_id" text,
    "item_key" text,
    "proposal_version" integer,
    "proposal_hash" text,
    "request_json" text,
    "request_hash" text,
    "quote_json" text,
    "price_signature" text,
    "expires_at" datetime,
    "approved_at" datetime,
    "revoked_at" datetime,
    "task_id" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "credit_accounts" (
    "user_id" text NOT NULL,
    "available_microcredits" integer,
    "reserved_microcredits" integer,
    "version" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("user_id")
);

CREATE TABLE IF NOT EXISTS "credit_ledger_entries" (
    "id" text NOT NULL,
    "user_id" text,
    "type" text,
    "amount_microcredits" integer,
    "available_delta_microcredits" integer,
    "reserved_delta_microcredits" integer,
    "available_after_microcredits" integer,
    "reserved_after_microcredits" integer,
    "billing_order_id" text,
    "payment_order_id" text,
    "redeem_code_id" text,
    "actor_user_id" text,
    "model" text,
    "channel_id" text,
    "scene" text,
    "note" text,
    "reference_key" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "email_verification_codes" (
    "id" text NOT NULL,
    "email" text,
    "code_hash" text,
    "purpose" text,
    "expires_at" datetime,
    "used_at" datetime,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "id_sequences" (
    "name" text NOT NULL,
    "value" integer,
    "updated_at" datetime,
    PRIMARY KEY ("name")
);

CREATE TABLE IF NOT EXISTS "logical_model_revisions" (
    "id" text NOT NULL,
    "logical_model_id" text,
    "version" integer,
    "capability_spec_json" text,
    "default_options_json" text,
    "created_by" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "logical_model_routes" (
    "id" text NOT NULL,
    "logical_model_revision_id" text,
    "channel_model_id" text,
    "enabled" numeric,
    "priority" integer,
    "weight" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "logical_models" (
    "id" text NOT NULL,
    "code" text,
    "name" text,
    "icon" text,
    "description" text,
    "capability" text,
    "enabled" numeric,
    "sort_order" integer,
    "revision_sequence" integer NOT NULL DEFAULT 0,
    "active_revision_id" text,
    "source_channel_model_id" text,
    "price_policy" text DEFAULT 'unified',
    "billing_mode" text,
    "unit_price_microcredits" integer,
    "input_price_microcredits" integer,
    "output_price_microcredits" integer,
    "cached_price_microcredits" integer,
    "legacy_model_ids_json" text,
    "archived_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "model_channels" (
    "id" text NOT NULL,
    "user_id" text,
    "scope" text,
    "enabled" numeric,
    "name" text,
    "public_alias" text NOT NULL DEFAULT '',
    "sort_order" integer NOT NULL DEFAULT 0,
    "base_url" text,
    "api_key" text,
    "secret_key" text,
    "api_format" text,
    "concurrency_limit" integer,
    "models_json" text,
    "retired_models_json" text,
    "headers_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    "deleted_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "model_pricings" (
    "id" text NOT NULL,
    "channel_id" text,
    "model" text,
    "capability" text,
    "currency" text,
    "input_per_million_micros" integer,
    "output_per_million_micros" integer,
    "cached_per_million_micros" integer,
    "per_request_micros" integer,
    "per_media_micros" integer,
    "per_video_second_micros" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "o_auth_states" (
    "id" text NOT NULL,
    "provider" text,
    "state_hash" text,
    "code_verifier" text,
    "next_path" text,
    "expires_at" datetime,
    "used_at" datetime,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_notifications" (
    "id" text NOT NULL,
    "provider_id" text,
    "provider_event_id" text,
    "provider_config_id" text,
    "merchant_order_no" text,
    "payment_order_id" text,
    "payload_digest" text,
    "payload_cipher" text,
    "normalized_json" text,
    "status" text,
    "attempts" integer,
    "last_error" text,
    "next_attempt_at" datetime,
    "processed_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_orders" (
    "id" text NOT NULL,
    "user_id" text,
    "idempotency_key" text,
    "merchant_order_no" text,
    "product_id" text,
    "product_name" text,
    "provider_id" text,
    "plugin_id" text,
    "plugin_version" text,
    "provider_config_id" text,
    "provider_config_version" integer,
    "amount_fen" integer,
    "currency" text,
    "credits_microcredits" integer,
    "status" text,
    "provider_trade_no" text,
    "provider_status" text,
    "checkout_mode" text,
    "checkout_value" text,
    "checkout_expires_at" datetime,
    "expires_at" datetime,
    "provider_paid_at" datetime,
    "credited_at" datetime,
    "closed_at" datetime,
    "last_queried_at" datetime,
    "last_error" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_provider_configs" (
    "id" text NOT NULL,
    "provider_id" text,
    "plugin_id" text,
    "plugin_version" text,
    "version" integer,
    "enabled" numeric,
    "close_after_minutes" integer,
    "config_cipher" text,
    "config_digest" text,
    "created_by" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_reconciliation_items" (
    "id" text NOT NULL,
    "run_id" text,
    "provider_id" text,
    "payment_order_id" text,
    "merchant_order_no" text,
    "provider_trade_no" text,
    "amount_fen" integer,
    "currency" text,
    "result" text,
    "resolved" numeric,
    "detail" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_reconciliation_runs" (
    "id" text NOT NULL,
    "provider_id" text,
    "config_id" text,
    "bill_date" text,
    "status" text,
    "total_items" integer,
    "match_items" integer,
    "recovered_items" integer,
    "error_items" integer,
    "error" text,
    "started_by" text,
    "started_at" datetime,
    "completed_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "plugin_platform_states" (
    "plugin_id" text NOT NULL,
    "available" numeric,
    "updated_by" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("plugin_id")
);

CREATE TABLE IF NOT EXISTS "production_task_links" (
    "id" text NOT NULL,
    "task_id" text,
    "project_id" text,
    "canvas_id" text,
    "unit_id" text,
    "shot_id" text,
    "workflow_step_id" text,
    "artifact_type" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_candidates" (
    "id" text NOT NULL,
    "project_id" text,
    "unit_id" text,
    "shot_id" text,
    "name" text,
    "name_key" text,
    "category" text,
    "status" text,
    "source" text,
    "details_json" text,
    "resolved_asset_id" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_folders" (
    "id" text NOT NULL,
    "project_id" text,
    "parent_id" text,
    "name" text,
    "name_key" text,
    "style" text,
    "theme" text,
    "position" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_links" (
    "id" text NOT NULL,
    "project_id" text,
    "asset_id" text,
    "folder_id" text,
    "position" integer,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_units" (
    "id" text NOT NULL,
    "project_id" text,
    "parent_id" text,
    "kind" text,
    "title" text,
    "source_text" text,
    "word_count" integer,
    "status" text,
    "position" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "projects" (
    "id" text NOT NULL,
    "user_id" text,
    "name" text,
    "type" text,
    "aspect_ratio" text,
    "source_type" text,
    "description" text,
    "cover_resource_id" text,
    "style_preset_id" text,
    "style_profile_json" text,
    "default_image_model" text,
    "default_video_model" text,
    "status" text,
    "revision" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "prompt_templates" (
    "id" text NOT NULL,
    "operation" text,
    "name" text,
    "version" integer,
    "content" text,
    "output_type" text,
    "enabled" numeric,
    "created_by" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "redeem_batches" (
    "id" text NOT NULL,
    "amount_microcredits" integer,
    "count" integer,
    "note" text,
    "created_by" text,
    "codes_cipher" text,
    "expires_at" datetime,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "redeem_codes" (
    "id" text NOT NULL,
    "batch_id" text,
    "code_hash" text,
    "code_suffix" text,
    "amount_microcredits" integer,
    "status" text,
    "redeemed_by" text,
    "redeemed_at" datetime,
    "redeemed_ip" text,
    "expires_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "resource_deletion_jobs" (
    "id" text NOT NULL,
    "user_id" text,
    "resource_id" text,
    "provider" text,
    "endpoint" text,
    "bucket" text,
    "storage_setting_id" text,
    "object_key" text,
    "status" text,
    "attempts" integer,
    "last_error" text,
    "next_attempt_at" datetime,
    "lease_owner" text,
    "lease_expires_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "resources" (
    "id" text NOT NULL,
    "user_id" text,
    "kind" text,
    "status" text,
    "provider" text,
    "endpoint" text,
    "bucket" text,
    "storage_setting_id" text,
    "object_key" text,
    "public_url" text,
    "mime_type" text,
    "size" integer,
    "width" integer,
    "height" integer,
    "duration_ms" integer,
    "e_tag" text,
    "playback_status" text,
    "playback_object_key" text,
    "playback_error" text,
    "upload_key" text,
    "error" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "results" (
    "id" text NOT NULL,
    "user_id" text,
    "task_id" text,
    "kind" text,
    "url" text,
    "payload" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "route_attempts" (
    "id" text NOT NULL,
    "task_id" text,
    "route_run" integer,
    "attempt_number" integer,
    "logical_model_id" text,
    "logical_model_revision_id" text,
    "route_id" text,
    "channel_model_id" text,
    "channel_id" text,
    "status" text,
    "dispatch_state" text,
    "provider_request_id" text,
    "failure_code" text,
    "failure_message" text,
    "started_at" datetime,
    "completed_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "schema_migrations" (
    "version" integer PRIMARY KEY AUTOINCREMENT,
    "name" text NOT NULL,
    "checksum" text NOT NULL,
    "applied_at" datetime NOT NULL
);

CREATE TABLE IF NOT EXISTS "shot_artifacts" (
    "id" text NOT NULL,
    "project_id" text,
    "unit_id" text,
    "shot_id" text,
    "revision_id" text,
    "task_id" text,
    "type" text,
    "version" integer,
    "resource_id" text,
    "status" text,
    "selected" numeric,
    "metadata_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shot_asset_references" (
    "id" text NOT NULL,
    "shot_id" text,
    "asset_version_id" text,
    "role" text,
    "status" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shot_revisions" (
    "id" text NOT NULL,
    "shot_id" text,
    "version" integer,
    "plot_description" text,
    "action" text,
    "dialogue" text,
    "shot_size" text,
    "camera_angle" text,
    "camera_movement" text,
    "duration_ms" integer,
    "image_prompt" text,
    "video_prompt" text,
    "negative_prompt" text,
    "continuity_notes" text,
    "action_beats_json" text,
    "created_by" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shots" (
    "id" text NOT NULL,
    "project_id" text,
    "unit_id" text,
    "current_revision_id" text,
    "title" text,
    "description" text,
    "position" integer,
    "duration_ms" integer,
    "status" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skill_files" (
    "id" text NOT NULL,
    "skill_version_id" text,
    "path" text,
    "kind" text,
    "mime_type" text,
    "size" integer,
    "sha256" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skill_versions" (
    "id" text NOT NULL,
    "skill_id" text,
    "version_label" text,
    "content_hash" text,
    "entry_path" text,
    "package_key" text,
    "file_count" integer,
    "total_bytes" integer,
    "source_commit" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skills" (
    "id" text NOT NULL,
    "owner_id" text,
    "author_name" text,
    "author_avatar_url" text,
    "name" text,
    "description" text,
    "instruction" text,
    "current_version_id" text,
    "version_label" text,
    "content_hash" text,
    "file_count" integer,
    "total_bytes" integer,
    "source_type" text,
    "source_url" text,
    "source_ref" text,
    "source_subdir" text,
    "source_commit" text,
    "sync_status" text,
    "sync_error" text,
    "auto_update" numeric,
    "last_checked_at" datetime,
    "last_synced_at" datetime,
    "status" integer,
    "source" integer,
    "tag" text,
    "sort_weight" integer,
    "is_private" numeric,
    "markdown_url" text,
    "showcase_media_json" text,
    "extra_info" text,
    "initial_like_count" integer,
    "initial_added_count" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "storage_locations" (
    "id" text NOT NULL,
    "scope" text,
    "owner_id" text,
    "provider" text,
    "location_digest" text,
    "value_json" text,
    "tested_digest" text,
    "tested_at" datetime,
    "active" numeric,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "style_profiles" (
    "id" text NOT NULL,
    "user_id" text,
    "name" text,
    "description" text,
    "cover_url" text,
    "tags_json" text,
    "profile_json" text,
    "favorite" numeric,
    "last_used_at" datetime,
    "revision" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "system_settings" (
    "key" text NOT NULL,
    "value_json" text,
    "updated_by" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("key")
);

CREATE TABLE IF NOT EXISTS "task_logs" (
    "id" text NOT NULL,
    "user_id" text,
    "task_id" text,
    "trace_id" text,
    "request_id" text,
    "level" text,
    "message" text,
    "payload" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "task_text_delta" (
    "id" text NOT NULL,
    "user_id" text,
    "task_id" text,
    "sequence" integer,
    "content" text,
    "byte_count" integer,
    "created_at" datetime,
    "expires_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "tasks" (
    "creation_submission_id" text,
    "id" text NOT NULL,
    "user_id" text,
    "trace_id" text,
    "request_id" text,
    "project_id" text,
    "type" text,
    "status" text,
    "stage" text,
    "progress" integer,
    "prompt" text,
    "operation" text,
    "provider" text,
    "model" text,
    "logical_model_id" text,
    "logical_model_revision_id" text,
    "route_id" text,
    "channel_model_id" text,
    "route_run" integer,
    "billing_order_id" text,
    "provider_request_id" text,
    "provider_cancel_status" text,
    "provider_cancel_error" text,
    "provider_cancel_attempts" integer,
    "provider_cancel_requested_at" datetime,
    "provider_cancelled_at" datetime,
    "provider_cancel_next_check_at" datetime,
    "poll_stage" text,
    "next_poll_at" datetime,
    "lease_owner" text,
    "lease_expires_at" datetime,
    "input_json" text,
    "result_json" text,
    "text_draft" text,
    "error" text,
    "attempts" integer,
    "started_at" datetime,
    "completed_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "topup_products" (
    "id" text NOT NULL,
    "name" text,
    "description" text,
    "amount_fen" integer,
    "credits_microcredits" integer,
    "enabled" numeric,
    "sort_order" integer,
    "created_by" text,
    "updated_by" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_announcement_reads" (
    "id" text NOT NULL,
    "user_id" text,
    "announcement_id" text,
    "read_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_daily_activities" (
    "id" text NOT NULL,
    "day" date,
    "user_id" text,
    "first_active_at" datetime,
    "last_active_at" datetime,
    "login_count" integer,
    "task_count" integer,
    "agent_message_count" integer,
    "canvas_active" numeric,
    "asset_count" integer,
    "resource_count" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_daily_upload_usages" (
    "id" text NOT NULL,
    "user_id" text,
    "day" text,
    "bytes" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_identities" (
    "id" text NOT NULL,
    "user_id" text,
    "provider" text,
    "subject" text,
    "provider_username" text,
    "avatar_url" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_oss_settings" (
    "id" text NOT NULL,
    "user_id" text,
    "enabled" numeric,
    "value_json" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_plugin_states" (
    "id" text NOT NULL,
    "user_id" text,
    "plugin_id" text,
    "enabled" numeric,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_prompt_customizations" (
    "id" text NOT NULL,
    "user_id" text,
    "operation" text,
    "mode" text,
    "content" text,
    "base_template_id" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_skill_states" (
    "id" text NOT NULL,
    "user_id" text,
    "skill_id" text,
    "installed_version_id" text,
    "auto_update" numeric,
    "added" numeric,
    "liked" numeric,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "users" (
    "id" text NOT NULL,
    "username" text,
    "email" text,
    "display_name" text,
    "role" text,
    "status" text,
    "password_hash" text,
    "last_login_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "voice_profiles" (
    "id" text NOT NULL,
    "user_id" text,
    "name" text,
    "provider" text,
    "voice_key" text,
    "language" text,
    "timbre" text,
    "sample_resource_id" text,
    "compatible_models_json" text,
    "status" text,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_instances" (
    "id" text NOT NULL,
    "project_id" text,
    "unit_id" text,
    "template_version_id" text,
    "scope" text,
    "status" text,
    "revision" integer,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_step_instances" (
    "id" text NOT NULL,
    "workflow_instance_id" text,
    "step_key" text,
    "name" text,
    "position" integer,
    "status" text,
    "input_json" text,
    "output_json" text,
    "error" text,
    "started_at" datetime,
    "completed_at" datetime,
    "created_at" datetime,
    "updated_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_step_tasks" (
    "id" text NOT NULL,
    "workflow_step_id" text,
    "task_id" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_template_versions" (
    "id" text NOT NULL,
    "template_key" text,
    "name" text,
    "version" integer,
    "definition_json" text,
    "created_at" datetime,
    PRIMARY KEY ("id")
);

-- 索引
CREATE INDEX IF NOT EXISTS "idx_admin_audit_events_actor_user_id" ON "admin_audit_events" ("actor_user_id");
CREATE INDEX IF NOT EXISTS "idx_admin_audit_events_action" ON "admin_audit_events" ("action");
CREATE INDEX IF NOT EXISTS "idx_admin_audit_events_target_type" ON "admin_audit_events" ("target_type");
CREATE INDEX IF NOT EXISTS "idx_admin_audit_events_target_id" ON "admin_audit_events" ("target_id");
CREATE INDEX IF NOT EXISTS "idx_admin_audit_events_created_at" ON "admin_audit_events" ("created_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_agent_profiles_scope" ON "agent_profiles" ("user_id", "scope", "project_id", "canvas_id");
CREATE INDEX IF NOT EXISTS "idx_agent_profiles_user_id" ON "agent_profiles" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_agent_profiles_hash" ON "agent_profiles" ("hash");
CREATE INDEX IF NOT EXISTS "idx_announcement_image_drafts_user_id" ON "announcement_image_drafts" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_announcement_image_drafts_created_at" ON "announcement_image_drafts" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_announcements_image_resource_id" ON "announcements" ("image_resource_id");
CREATE INDEX IF NOT EXISTS "idx_announcements_level" ON "announcements" ("level");
CREATE INDEX IF NOT EXISTS "idx_announcements_pinned" ON "announcements" ("pinned");
CREATE INDEX IF NOT EXISTS "idx_announcements_status" ON "announcements" ("status");
CREATE INDEX IF NOT EXISTS "idx_announcements_status_published" ON "announcements" ("status", "published_at");
CREATE INDEX IF NOT EXISTS "idx_announcements_created_by" ON "announcements" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_user_id" ON "api_call_logs" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_api_logs_user_created" ON "api_call_logs" ("user_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_trace_id" ON "api_call_logs" ("trace_id");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_request_id" ON "api_call_logs" ("request_id");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_channel_id" ON "api_call_logs" ("channel_id");
CREATE INDEX IF NOT EXISTS "idx_api_logs_channel_created" ON "api_call_logs" ("channel_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_task_id" ON "api_call_logs" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_billing_order_id" ON "api_call_logs" ("billing_order_id");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_source" ON "api_call_logs" ("source");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_capability" ON "api_call_logs" ("capability");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_request_kind" ON "api_call_logs" ("request_kind");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_billable" ON "api_call_logs" ("billable");
CREATE INDEX IF NOT EXISTS "idx_api_logs_model_created" ON "api_call_logs" ("model", "created_at");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_status" ON "api_call_logs" ("status");
CREATE INDEX IF NOT EXISTS "idx_api_logs_status_created" ON "api_call_logs" ("status", "created_at");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_error_code" ON "api_call_logs" ("error_code");
CREATE INDEX IF NOT EXISTS "idx_api_call_logs_created_at" ON "api_call_logs" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_ark_private_asset_bindings_user_id" ON "ark_private_asset_bindings" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_ark_private_asset_bindings_resource_id" ON "ark_private_asset_bindings" ("resource_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_ark_private_asset_binding_resource_project" ON "ark_private_asset_bindings" ("resource_id", "project_name");
CREATE INDEX IF NOT EXISTS "idx_ark_private_asset_bindings_asset_group_id" ON "ark_private_asset_bindings" ("asset_group_id");
CREATE INDEX IF NOT EXISTS "idx_ark_private_asset_bindings_ark_asset_id" ON "ark_private_asset_bindings" ("ark_asset_id");
CREATE INDEX IF NOT EXISTS "idx_ark_private_asset_bindings_status" ON "ark_private_asset_bindings" ("status");
CREATE INDEX IF NOT EXISTS "idx_asset_folders_user_id" ON "asset_folders" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_asset_folders_user_name" ON "asset_folders" ("user_id", "name_key");
CREATE INDEX IF NOT EXISTS "idx_asset_folders_position" ON "asset_folders" ("position");
CREATE INDEX IF NOT EXISTS "idx_asset_representations_task_id" ON "asset_representations" ("task_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_asset_representations_task_role" ON "asset_representations" ("task_id", "role");
CREATE INDEX IF NOT EXISTS "idx_asset_representations_asset_version_id" ON "asset_representations" ("asset_version_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_asset_representations_version_role" ON "asset_representations" ("asset_version_id", "role");
CREATE INDEX IF NOT EXISTS "idx_asset_representations_resource_id" ON "asset_representations" ("resource_id");
CREATE INDEX IF NOT EXISTS "idx_asset_representations_media_type" ON "asset_representations" ("media_type");
CREATE INDEX IF NOT EXISTS "idx_asset_representations_role" ON "asset_representations" ("role");
CREATE INDEX IF NOT EXISTS "idx_asset_versions_asset_id" ON "asset_versions" ("asset_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_asset_versions_number" ON "asset_versions" ("asset_id", "version");
CREATE INDEX IF NOT EXISTS "idx_asset_versions_status" ON "asset_versions" ("status");
CREATE INDEX IF NOT EXISTS "idx_assets_user_id" ON "assets" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_assets_user_updated" ON "assets" ("user_id", "updated_at");
CREATE INDEX IF NOT EXISTS "idx_assets_folder_id" ON "assets" ("folder_id");
CREATE INDEX IF NOT EXISTS "idx_assets_kind" ON "assets" ("kind");
CREATE INDEX IF NOT EXISTS "idx_assets_category" ON "assets" ("category");
CREATE INDEX IF NOT EXISTS "idx_assets_status" ON "assets" ("status");
CREATE INDEX IF NOT EXISTS "idx_assets_primary_version_id" ON "assets" ("primary_version_id");
CREATE INDEX IF NOT EXISTS "idx_auth_sessions_user_id" ON "auth_sessions" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_auth_sessions_expires_at" ON "auth_sessions" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_user_id" ON "billing_orders" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_billing_user_idempotency" ON "billing_orders" ("user_id", "idempotency_key");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_task_id" ON "billing_orders" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_channel_id" ON "billing_orders" ("channel_id");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_channel_model_id" ON "billing_orders" ("channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_price_tier_id" ON "billing_orders" ("price_tier_id");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_model" ON "billing_orders" ("model");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_capability" ON "billing_orders" ("capability");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_scene" ON "billing_orders" ("scene");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_status" ON "billing_orders" ("status");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_provider_request_id" ON "billing_orders" ("provider_request_id");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_resolved_by" ON "billing_orders" ("resolved_by");
CREATE INDEX IF NOT EXISTS "idx_billing_orders_created_at" ON "billing_orders" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_canvas_projects_user_id" ON "canvas_projects" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_projects_user_updated" ON "canvas_projects" ("user_id", "updated_at");
CREATE INDEX IF NOT EXISTS "idx_canvas_projects_user_project_updated" ON "canvas_projects" ("user_id", "project_id", "updated_at");
CREATE INDEX IF NOT EXISTS "idx_canvas_projects_project_id" ON "canvas_projects" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_shares_user_id" ON "canvas_shares" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_canvas_share_owner_project" ON "canvas_shares" ("user_id", "project_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_shares_project_id" ON "canvas_shares" ("project_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_canvas_shares_token_hash" ON "canvas_shares" ("token_hash");
CREATE INDEX IF NOT EXISTS "idx_canvas_shares_enabled" ON "canvas_shares" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_canvas_shares_expires_at" ON "canvas_shares" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_canvas_unit_links_project_id" ON "canvas_unit_links" ("project_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_canvas_unit_links_unique" ON "canvas_unit_links" ("project_id", "canvas_id", "unit_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_unit_links_project_unit" ON "canvas_unit_links" ("project_id", "unit_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_unit_links_canvas_id" ON "canvas_unit_links" ("canvas_id");
CREATE INDEX IF NOT EXISTS "idx_canvas_unit_links_unit_id" ON "canvas_unit_links" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_channel_model_price_tiers_channel_model_id" ON "channel_model_price_tiers" ("channel_model_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_channel_model_price_tier_active" ON "channel_model_price_tiers" ("channel_model_id", "selector_key") WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS "idx_channel_model_price_tiers_price_configured" ON "channel_model_price_tiers" ("price_configured");
CREATE INDEX IF NOT EXISTS "idx_channel_model_price_tiers_enabled" ON "channel_model_price_tiers" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_channel_model_price_tiers_deleted_at" ON "channel_model_price_tiers" ("deleted_at");
CREATE INDEX IF NOT EXISTS "idx_channel_models_channel_id" ON "channel_models" ("channel_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_channel_model_key_active" ON "channel_models" ("channel_id", "model_key") WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS "idx_channel_models_capability" ON "channel_models" ("capability");
CREATE INDEX IF NOT EXISTS "idx_channel_models_protocol" ON "channel_models" ("protocol");
CREATE INDEX IF NOT EXISTS "idx_channel_models_price_configured" ON "channel_models" ("price_configured");
CREATE INDEX IF NOT EXISTS "idx_channel_models_enabled" ON "channel_models" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_channel_models_deleted_at" ON "channel_models" ("deleted_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_character_voice_bindings_asset_version_id" ON "character_voice_bindings" ("asset_version_id");
CREATE INDEX IF NOT EXISTS "idx_character_voice_bindings_voice_profile_id" ON "character_voice_bindings" ("voice_profile_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_run_id" ON "cloud_agent_canvas_mutations" ("run_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_user_id" ON "cloud_agent_canvas_mutations" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_canvas_id" ON "cloud_agent_canvas_mutations" ("canvas_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_step_id" ON "cloud_agent_canvas_mutations" ("step_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_status" ON "cloud_agent_canvas_mutations" ("status");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_canvas_mutations_created_at" ON "cloud_agent_canvas_mutations" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_executions_user_id" ON "cloud_agent_executions" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_executions_status" ON "cloud_agent_executions" ("status");
CREATE INDEX IF NOT EXISTS "idx_cloud_agent_executions_cleanup_pending" ON "cloud_agent_executions" ("cleanup_pending");
CREATE INDEX IF NOT EXISTS "idx_creation_runs_user_id" ON "creation_runs" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_creation_client" ON "creation_runs" ("user_id", "client_key");
CREATE INDEX IF NOT EXISTS "idx_creation_submissions_user_id" ON "creation_submissions" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_creation_item" ON "creation_submissions" ("run_id", "item_key");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_creation_submissions_task_id" ON "creation_submissions" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_user_id" ON "credit_ledger_entries" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_user_created" ON "credit_ledger_entries" ("user_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_type" ON "credit_ledger_entries" ("type");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_billing_order_id" ON "credit_ledger_entries" ("billing_order_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_payment_order_id" ON "credit_ledger_entries" ("payment_order_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_redeem_code_id" ON "credit_ledger_entries" ("redeem_code_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_actor_user_id" ON "credit_ledger_entries" ("actor_user_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_model" ON "credit_ledger_entries" ("model");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_channel_id" ON "credit_ledger_entries" ("channel_id");
CREATE INDEX IF NOT EXISTS "idx_credit_ledger_entries_scene" ON "credit_ledger_entries" ("scene");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_credit_ledger_entries_reference_key" ON "credit_ledger_entries" ("reference_key");
CREATE INDEX IF NOT EXISTS "idx_email_verification_codes_email" ON "email_verification_codes" ("email");
CREATE INDEX IF NOT EXISTS "idx_email_verification_codes_purpose" ON "email_verification_codes" ("purpose");
CREATE INDEX IF NOT EXISTS "idx_email_verification_codes_expires_at" ON "email_verification_codes" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_email_verification_codes_used_at" ON "email_verification_codes" ("used_at");
CREATE INDEX IF NOT EXISTS "idx_email_verification_codes_created_at" ON "email_verification_codes" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_logical_model_revisions_logical_model_id" ON "logical_model_revisions" ("logical_model_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_logical_revision_version" ON "logical_model_revisions" ("logical_model_id", "version");
CREATE INDEX IF NOT EXISTS "idx_logical_model_revisions_created_by" ON "logical_model_revisions" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_logical_model_routes_logical_model_revision_id" ON "logical_model_routes" ("logical_model_revision_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_logical_route_member" ON "logical_model_routes" ("logical_model_revision_id", "channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_logical_model_routes_channel_model_id" ON "logical_model_routes" ("channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_logical_model_routes_enabled" ON "logical_model_routes" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_logical_model_routes_priority" ON "logical_model_routes" ("priority");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_logical_models_code" ON "logical_models" ("code") WHERE archived_at IS NULL;
CREATE INDEX IF NOT EXISTS "idx_logical_models_capability" ON "logical_models" ("capability");
CREATE INDEX IF NOT EXISTS "idx_logical_models_enabled" ON "logical_models" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_logical_models_sort_order" ON "logical_models" ("sort_order");
CREATE INDEX IF NOT EXISTS "idx_logical_models_active_revision_id" ON "logical_models" ("active_revision_id");
CREATE INDEX IF NOT EXISTS "idx_logical_models_source_channel_model_id" ON "logical_models" ("source_channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_logical_models_archived_at" ON "logical_models" ("archived_at");
CREATE INDEX IF NOT EXISTS "idx_model_channels_user_id" ON "model_channels" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_model_channels_scope" ON "model_channels" ("scope");
CREATE INDEX IF NOT EXISTS "idx_model_channels_enabled" ON "model_channels" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_model_channels_deleted_at" ON "model_channels" ("deleted_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_model_pricing_scope" ON "model_pricings" ("channel_id", "model", "capability");
CREATE INDEX IF NOT EXISTS "idx_o_auth_states_provider" ON "o_auth_states" ("provider");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_o_auth_states_state_hash" ON "o_auth_states" ("state_hash");
CREATE INDEX IF NOT EXISTS "idx_o_auth_states_expires_at" ON "o_auth_states" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_o_auth_states_used_at" ON "o_auth_states" ("used_at");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_provider_id" ON "payment_notifications" ("provider_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_provider_event" ON "payment_notifications" ("provider_id", "provider_event_id");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_provider_config_id" ON "payment_notifications" ("provider_config_id");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_merchant_order_no" ON "payment_notifications" ("merchant_order_no");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_payment_order_id" ON "payment_notifications" ("payment_order_id");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_status" ON "payment_notifications" ("status");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_next_attempt_at" ON "payment_notifications" ("next_attempt_at");
CREATE INDEX IF NOT EXISTS "idx_payment_notifications_created_at" ON "payment_notifications" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_user_id" ON "payment_orders" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_user_idempotency" ON "payment_orders" ("user_id", "idempotency_key");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_orders_merchant_order_no" ON "payment_orders" ("merchant_order_no");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_product_id" ON "payment_orders" ("product_id");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_provider_id" ON "payment_orders" ("provider_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_provider_trade" ON "payment_orders" ("provider_id", "provider_trade_no");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_plugin_id" ON "payment_orders" ("plugin_id");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_provider_config_id" ON "payment_orders" ("provider_config_id");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_status" ON "payment_orders" ("status");
CREATE INDEX IF NOT EXISTS "idx_payment_order_status_expiry" ON "payment_orders" ("status", "expires_at");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_provider_status" ON "payment_orders" ("provider_status");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_provider_paid_at" ON "payment_orders" ("provider_paid_at");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_credited_at" ON "payment_orders" ("credited_at");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_closed_at" ON "payment_orders" ("closed_at");
CREATE INDEX IF NOT EXISTS "idx_payment_orders_created_at" ON "payment_orders" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_payment_provider_configs_provider_id" ON "payment_provider_configs" ("provider_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_provider_version" ON "payment_provider_configs" ("provider_id", "version");
CREATE INDEX IF NOT EXISTS "idx_payment_provider_configs_plugin_id" ON "payment_provider_configs" ("plugin_id");
CREATE INDEX IF NOT EXISTS "idx_payment_provider_configs_enabled" ON "payment_provider_configs" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_payment_provider_configs_created_by" ON "payment_provider_configs" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_payment_provider_configs_created_at" ON "payment_provider_configs" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_run_id" ON "payment_reconciliation_items" ("run_id");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_provider_id" ON "payment_reconciliation_items" ("provider_id");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_payment_order_id" ON "payment_reconciliation_items" ("payment_order_id");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_merchant_order_no" ON "payment_reconciliation_items" ("merchant_order_no");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_provider_trade_no" ON "payment_reconciliation_items" ("provider_trade_no");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_result" ON "payment_reconciliation_items" ("result");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_items_resolved" ON "payment_reconciliation_items" ("resolved");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_runs_provider_id" ON "payment_reconciliation_runs" ("provider_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_payment_reconciliation_date" ON "payment_reconciliation_runs" ("provider_id", "bill_date");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_runs_config_id" ON "payment_reconciliation_runs" ("config_id");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_runs_bill_date" ON "payment_reconciliation_runs" ("bill_date");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_runs_status" ON "payment_reconciliation_runs" ("status");
CREATE INDEX IF NOT EXISTS "idx_payment_reconciliation_runs_started_by" ON "payment_reconciliation_runs" ("started_by");
CREATE INDEX IF NOT EXISTS "idx_plugin_platform_states_available" ON "plugin_platform_states" ("available");
CREATE INDEX IF NOT EXISTS "idx_plugin_platform_states_updated_by" ON "plugin_platform_states" ("updated_by");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_task_id" ON "production_task_links" ("task_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_production_task_context" ON "production_task_links" ("task_id", "shot_id", "artifact_type");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_project_id" ON "production_task_links" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_canvas_id" ON "production_task_links" ("canvas_id");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_unit_id" ON "production_task_links" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_shot_id" ON "production_task_links" ("shot_id");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_workflow_step_id" ON "production_task_links" ("workflow_step_id");
CREATE INDEX IF NOT EXISTS "idx_production_task_links_artifact_type" ON "production_task_links" ("artifact_type");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_project_id" ON "project_asset_candidates" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_project_unit_status" ON "project_asset_candidates" ("project_id", "unit_id", "status");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_project_status_category" ON "project_asset_candidates" ("project_id", "status", "category");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_unit_id" ON "project_asset_candidates" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_shot_id" ON "project_asset_candidates" ("shot_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_name_key" ON "project_asset_candidates" ("name_key");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_category" ON "project_asset_candidates" ("category");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_status" ON "project_asset_candidates" ("status");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_source" ON "project_asset_candidates" ("source");
CREATE INDEX IF NOT EXISTS "idx_project_asset_candidates_resolved_asset_id" ON "project_asset_candidates" ("resolved_asset_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_folders_project_id" ON "project_asset_folders" ("project_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_project_asset_folders_sibling_name" ON "project_asset_folders" ("project_id", "parent_id", "name_key");
CREATE INDEX IF NOT EXISTS "idx_project_asset_folders_parent_id" ON "project_asset_folders" ("parent_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_folders_position" ON "project_asset_folders" ("position");
CREATE INDEX IF NOT EXISTS "idx_project_asset_links_project_id" ON "project_asset_links" ("project_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_project_asset_links_unique" ON "project_asset_links" ("project_id", "asset_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_links_project_folder_position" ON "project_asset_links" ("project_id", "folder_id", "position");
CREATE INDEX IF NOT EXISTS "idx_project_asset_links_asset_id" ON "project_asset_links" ("asset_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_links_folder_id" ON "project_asset_links" ("folder_id");
CREATE INDEX IF NOT EXISTS "idx_project_asset_links_position" ON "project_asset_links" ("position");
CREATE INDEX IF NOT EXISTS "idx_project_units_project_id" ON "project_units" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_project_units_project_position" ON "project_units" ("project_id", "position");
CREATE INDEX IF NOT EXISTS "idx_project_units_parent_id" ON "project_units" ("parent_id");
CREATE INDEX IF NOT EXISTS "idx_project_units_kind" ON "project_units" ("kind");
CREATE INDEX IF NOT EXISTS "idx_project_units_status" ON "project_units" ("status");
CREATE INDEX IF NOT EXISTS "idx_projects_user_id" ON "projects" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_projects_user_name" ON "projects" ("user_id", "name");
CREATE INDEX IF NOT EXISTS "idx_projects_type" ON "projects" ("type");
CREATE INDEX IF NOT EXISTS "idx_projects_cover_resource_id" ON "projects" ("cover_resource_id");
CREATE INDEX IF NOT EXISTS "idx_projects_status" ON "projects" ("status");
CREATE INDEX IF NOT EXISTS "idx_projects_updated_at" ON "projects" ("updated_at");
CREATE INDEX IF NOT EXISTS "idx_prompt_templates_operation" ON "prompt_templates" ("operation");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_prompt_template_operation_version" ON "prompt_templates" ("operation", "version");
CREATE INDEX IF NOT EXISTS "idx_prompt_templates_enabled" ON "prompt_templates" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_prompt_template_active" ON "prompt_templates" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_prompt_templates_created_by" ON "prompt_templates" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_redeem_batches_created_by" ON "redeem_batches" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_redeem_batches_expires_at" ON "redeem_batches" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_redeem_batches_created_at" ON "redeem_batches" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_batch_id" ON "redeem_codes" ("batch_id");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_batch_status" ON "redeem_codes" ("batch_id", "status");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_batch_created" ON "redeem_codes" ("batch_id", "created_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_redeem_codes_code_hash" ON "redeem_codes" ("code_hash");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_status" ON "redeem_codes" ("status");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_redeemed_by" ON "redeem_codes" ("redeemed_by");
CREATE INDEX IF NOT EXISTS "idx_redeem_codes_expires_at" ON "redeem_codes" ("expires_at");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_user_id" ON "resource_deletion_jobs" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_resource_id" ON "resource_deletion_jobs" ("resource_id");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_storage_setting_id" ON "resource_deletion_jobs" ("storage_setting_id");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_object_key" ON "resource_deletion_jobs" ("object_key");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_due" ON "resource_deletion_jobs" ("status", "next_attempt_at");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_lease_owner" ON "resource_deletion_jobs" ("lease_owner");
CREATE INDEX IF NOT EXISTS "idx_resource_deletion_jobs_lease_expires_at" ON "resource_deletion_jobs" ("lease_expires_at");
CREATE INDEX IF NOT EXISTS "idx_resources_user_id" ON "resources" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_resources_user_created" ON "resources" ("user_id", "created_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_resources_user_upload_key" ON "resources" ("user_id", "upload_key");
CREATE INDEX IF NOT EXISTS "idx_resources_kind" ON "resources" ("kind");
CREATE INDEX IF NOT EXISTS "idx_resources_status" ON "resources" ("status");
CREATE INDEX IF NOT EXISTS "idx_resources_storage_setting_id" ON "resources" ("storage_setting_id");
CREATE INDEX IF NOT EXISTS "idx_resources_object_key" ON "resources" ("object_key");
CREATE INDEX IF NOT EXISTS "idx_resources_playback_status" ON "resources" ("playback_status");
CREATE INDEX IF NOT EXISTS "idx_results_user_id" ON "results" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_results_task_id" ON "results" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_task_id" ON "route_attempts" ("task_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_route_attempt_run_number" ON "route_attempts" ("task_id", "route_run", "attempt_number");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_route_run" ON "route_attempts" ("route_run");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_logical_model_id" ON "route_attempts" ("logical_model_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_logical_model_revision_id" ON "route_attempts" ("logical_model_revision_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_route_id" ON "route_attempts" ("route_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_channel_model_id" ON "route_attempts" ("channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_channel_id" ON "route_attempts" ("channel_id");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_status" ON "route_attempts" ("status");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_dispatch_state" ON "route_attempts" ("dispatch_state");
CREATE INDEX IF NOT EXISTS "idx_route_attempts_provider_request_id" ON "route_attempts" ("provider_request_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_project_id" ON "shot_artifacts" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_project_unit" ON "shot_artifacts" ("project_id", "unit_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_unit_id" ON "shot_artifacts" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_shot_id" ON "shot_artifacts" ("shot_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_shot_artifacts_version" ON "shot_artifacts" ("shot_id", "type", "version");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_revision_id" ON "shot_artifacts" ("revision_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_task_id" ON "shot_artifacts" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_type" ON "shot_artifacts" ("type");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_resource_id" ON "shot_artifacts" ("resource_id");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_status" ON "shot_artifacts" ("status");
CREATE INDEX IF NOT EXISTS "idx_shot_artifacts_selected" ON "shot_artifacts" ("selected");
CREATE INDEX IF NOT EXISTS "idx_shot_asset_references_shot_id" ON "shot_asset_references" ("shot_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_shot_asset_reference_unique" ON "shot_asset_references" ("shot_id", "asset_version_id", "role");
CREATE INDEX IF NOT EXISTS "idx_shot_asset_references_asset_version_id" ON "shot_asset_references" ("asset_version_id");
CREATE INDEX IF NOT EXISTS "idx_shot_asset_references_role" ON "shot_asset_references" ("role");
CREATE INDEX IF NOT EXISTS "idx_shot_asset_references_status" ON "shot_asset_references" ("status");
CREATE INDEX IF NOT EXISTS "idx_shot_revisions_shot_id" ON "shot_revisions" ("shot_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_shot_revisions_version" ON "shot_revisions" ("shot_id", "version");
CREATE INDEX IF NOT EXISTS "idx_shot_revisions_created_by" ON "shot_revisions" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_shots_project_id" ON "shots" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_shots_project_unit_position" ON "shots" ("project_id", "unit_id", "position");
CREATE INDEX IF NOT EXISTS "idx_shots_unit_id" ON "shots" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_shots_current_revision_id" ON "shots" ("current_revision_id");
CREATE INDEX IF NOT EXISTS "idx_shots_status" ON "shots" ("status");
CREATE INDEX IF NOT EXISTS "idx_skill_files_skill_version_id" ON "skill_files" ("skill_version_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_skill_version_file_path" ON "skill_files" ("skill_version_id", "path");
CREATE INDEX IF NOT EXISTS "idx_skill_files_kind" ON "skill_files" ("kind");
CREATE INDEX IF NOT EXISTS "idx_skill_versions_skill_id" ON "skill_versions" ("skill_id");
CREATE INDEX IF NOT EXISTS "idx_skill_versions_content_hash" ON "skill_versions" ("content_hash");
CREATE INDEX IF NOT EXISTS "idx_skill_versions_created_at" ON "skill_versions" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_skills_owner_id" ON "skills" ("owner_id");
CREATE INDEX IF NOT EXISTS "idx_skills_author_name" ON "skills" ("author_name");
CREATE INDEX IF NOT EXISTS "idx_skills_name" ON "skills" ("name");
CREATE INDEX IF NOT EXISTS "idx_skills_current_version_id" ON "skills" ("current_version_id");
CREATE INDEX IF NOT EXISTS "idx_skills_content_hash" ON "skills" ("content_hash");
CREATE INDEX IF NOT EXISTS "idx_skills_source_type" ON "skills" ("source_type");
CREATE INDEX IF NOT EXISTS "idx_skills_sync_status" ON "skills" ("sync_status");
CREATE INDEX IF NOT EXISTS "idx_skills_auto_update" ON "skills" ("auto_update");
CREATE INDEX IF NOT EXISTS "idx_skills_status" ON "skills" ("status");
CREATE INDEX IF NOT EXISTS "idx_skills_source" ON "skills" ("source");
CREATE INDEX IF NOT EXISTS "idx_skills_tag" ON "skills" ("tag");
CREATE INDEX IF NOT EXISTS "idx_skills_sort_weight" ON "skills" ("sort_weight");
CREATE INDEX IF NOT EXISTS "idx_skills_is_private" ON "skills" ("is_private");
CREATE INDEX IF NOT EXISTS "idx_skills_created_at" ON "skills" ("created_at");
CREATE INDEX IF NOT EXISTS "idx_skills_updated_at" ON "skills" ("updated_at");
CREATE INDEX IF NOT EXISTS "idx_storage_locations_scope_owner_active" ON "storage_locations" ("scope", "owner_id", "active");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_storage_locations_identity" ON "storage_locations" ("scope", "owner_id", "provider", "location_digest");
CREATE INDEX IF NOT EXISTS "idx_storage_locations_provider" ON "storage_locations" ("provider");
CREATE INDEX IF NOT EXISTS "idx_style_profiles_user_id" ON "style_profiles" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_style_profiles_user_updated" ON "style_profiles" ("user_id", "updated_at");
CREATE INDEX IF NOT EXISTS "idx_style_profiles_favorite" ON "style_profiles" ("favorite");
CREATE INDEX IF NOT EXISTS "idx_style_profiles_last_used_at" ON "style_profiles" ("last_used_at");
CREATE INDEX IF NOT EXISTS "idx_system_settings_updated_by" ON "system_settings" ("updated_by");
CREATE INDEX IF NOT EXISTS "idx_task_logs_user_id" ON "task_logs" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_task_logs_task_id" ON "task_logs" ("task_id");
CREATE INDEX IF NOT EXISTS "idx_task_logs_trace_id" ON "task_logs" ("trace_id");
CREATE INDEX IF NOT EXISTS "idx_task_logs_request_id" ON "task_logs" ("request_id");
CREATE INDEX IF NOT EXISTS "idx_task_text_delta_user_id" ON "task_text_delta" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_task_text_deltas_user_created" ON "task_text_delta" ("user_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_task_text_delta_task_id" ON "task_text_delta" ("task_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_task_text_deltas_sequence" ON "task_text_delta" ("task_id", "sequence");
CREATE INDEX IF NOT EXISTS "idx_task_text_delta_expires_at" ON "task_text_delta" ("expires_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_tasks_creation_submission_id" ON "tasks" ("creation_submission_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_user_id" ON "tasks" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_user_created" ON "tasks" ("user_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_user_project_created" ON "tasks" ("user_id", "project_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_trace_id" ON "tasks" ("trace_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_request_id" ON "tasks" ("request_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_project_id" ON "tasks" ("project_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_type" ON "tasks" ("type");
CREATE INDEX IF NOT EXISTS "idx_tasks_status" ON "tasks" ("status");
CREATE INDEX IF NOT EXISTS "idx_tasks_status_created" ON "tasks" ("status", "created_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_claim" ON "tasks" ("status", "lease_expires_at", "created_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_provider_cancel" ON "tasks" ("status", "provider_cancel_status", "provider_cancel_next_check_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_logical_model_id" ON "tasks" ("logical_model_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_logical_model_revision_id" ON "tasks" ("logical_model_revision_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_route_id" ON "tasks" ("route_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_channel_model_id" ON "tasks" ("channel_model_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_route_run" ON "tasks" ("route_run");
CREATE INDEX IF NOT EXISTS "idx_tasks_billing_order_id" ON "tasks" ("billing_order_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_provider_request_id" ON "tasks" ("provider_request_id");
CREATE INDEX IF NOT EXISTS "idx_tasks_provider_cancel_status" ON "tasks" ("provider_cancel_status");
CREATE INDEX IF NOT EXISTS "idx_tasks_next_poll_at" ON "tasks" ("next_poll_at");
CREATE INDEX IF NOT EXISTS "idx_tasks_lease_owner" ON "tasks" ("lease_owner");
CREATE INDEX IF NOT EXISTS "idx_tasks_lease_expires_at" ON "tasks" ("lease_expires_at");
CREATE INDEX IF NOT EXISTS "idx_topup_products_amount_fen" ON "topup_products" ("amount_fen");
CREATE INDEX IF NOT EXISTS "idx_topup_products_enabled" ON "topup_products" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_topup_products_sort_order" ON "topup_products" ("sort_order");
CREATE INDEX IF NOT EXISTS "idx_topup_products_created_by" ON "topup_products" ("created_by");
CREATE INDEX IF NOT EXISTS "idx_topup_products_updated_by" ON "topup_products" ("updated_by");
CREATE INDEX IF NOT EXISTS "idx_user_announcement_reads_user_id" ON "user_announcement_reads" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_announcement_read" ON "user_announcement_reads" ("user_id", "announcement_id");
CREATE INDEX IF NOT EXISTS "idx_user_announcement_reads_announcement_id" ON "user_announcement_reads" ("announcement_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_daily_activity_day_user" ON "user_daily_activities" ("day", "user_id");
CREATE INDEX IF NOT EXISTS "idx_user_daily_activities_day" ON "user_daily_activities" ("day");
CREATE INDEX IF NOT EXISTS "idx_user_daily_activities_user_id" ON "user_daily_activities" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_user_daily_upload_usages_user_id" ON "user_daily_upload_usages" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_daily_upload_day" ON "user_daily_upload_usages" ("user_id", "day");
CREATE INDEX IF NOT EXISTS "idx_user_daily_upload_usages_day" ON "user_daily_upload_usages" ("day");
CREATE INDEX IF NOT EXISTS "idx_user_identities_user_id" ON "user_identities" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_identity_provider_subject" ON "user_identities" ("provider", "subject");
CREATE INDEX IF NOT EXISTS "idx_user_oss_settings_user_id" ON "user_oss_settings" ("user_id");
CREATE INDEX IF NOT EXISTS "idx_user_oss_settings_user_created" ON "user_oss_settings" ("user_id", "created_at");
CREATE INDEX IF NOT EXISTS "idx_user_oss_settings_enabled" ON "user_oss_settings" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_user_plugin_states_user_id" ON "user_plugin_states" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_plugin_state_user_plugin" ON "user_plugin_states" ("user_id", "plugin_id");
CREATE INDEX IF NOT EXISTS "idx_user_plugin_states_plugin_id" ON "user_plugin_states" ("plugin_id");
CREATE INDEX IF NOT EXISTS "idx_user_plugin_states_enabled" ON "user_plugin_states" ("enabled");
CREATE INDEX IF NOT EXISTS "idx_user_prompt_customizations_user_id" ON "user_prompt_customizations" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_prompt_operation" ON "user_prompt_customizations" ("user_id", "operation");
CREATE INDEX IF NOT EXISTS "idx_user_prompt_customizations_operation" ON "user_prompt_customizations" ("operation");
CREATE INDEX IF NOT EXISTS "idx_user_prompt_customizations_base_template_id" ON "user_prompt_customizations" ("base_template_id");
CREATE INDEX IF NOT EXISTS "idx_user_skill_states_user_id" ON "user_skill_states" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_user_skill_state_user_skill" ON "user_skill_states" ("user_id", "skill_id");
CREATE INDEX IF NOT EXISTS "idx_user_skill_states_skill_id" ON "user_skill_states" ("skill_id");
CREATE INDEX IF NOT EXISTS "idx_user_skill_states_installed_version_id" ON "user_skill_states" ("installed_version_id");
CREATE INDEX IF NOT EXISTS "idx_user_skill_states_added" ON "user_skill_states" ("added");
CREATE INDEX IF NOT EXISTS "idx_user_skill_states_liked" ON "user_skill_states" ("liked");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_users_username" ON "users" ("username");
CREATE INDEX IF NOT EXISTS "idx_users_role" ON "users" ("role");
CREATE INDEX IF NOT EXISTS "idx_users_status" ON "users" ("status");
CREATE INDEX IF NOT EXISTS "idx_voice_profiles_user_id" ON "voice_profiles" ("user_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_voice_profiles_user_provider_key" ON "voice_profiles" ("user_id", "provider", "voice_key");
CREATE INDEX IF NOT EXISTS "idx_voice_profiles_sample_resource_id" ON "voice_profiles" ("sample_resource_id");
CREATE INDEX IF NOT EXISTS "idx_voice_profiles_status" ON "voice_profiles" ("status");
CREATE INDEX IF NOT EXISTS "idx_workflow_instances_project_id" ON "workflow_instances" ("project_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_workflow_instance_scope" ON "workflow_instances" ("project_id", "unit_id", "template_version_id");
CREATE INDEX IF NOT EXISTS "idx_workflow_instances_unit_id" ON "workflow_instances" ("unit_id");
CREATE INDEX IF NOT EXISTS "idx_workflow_instances_template_version_id" ON "workflow_instances" ("template_version_id");
CREATE INDEX IF NOT EXISTS "idx_workflow_instances_scope" ON "workflow_instances" ("scope");
CREATE INDEX IF NOT EXISTS "idx_workflow_instances_status" ON "workflow_instances" ("status");
CREATE INDEX IF NOT EXISTS "idx_workflow_step_instances_workflow_instance_id" ON "workflow_step_instances" ("workflow_instance_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_workflow_steps_instance_key" ON "workflow_step_instances" ("workflow_instance_id", "step_key");
CREATE INDEX IF NOT EXISTS "idx_workflow_step_instances_status" ON "workflow_step_instances" ("status");
CREATE INDEX IF NOT EXISTS "idx_workflow_step_tasks_workflow_step_id" ON "workflow_step_tasks" ("workflow_step_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_workflow_step_tasks_unique" ON "workflow_step_tasks" ("workflow_step_id", "task_id");
CREATE INDEX IF NOT EXISTS "idx_workflow_step_tasks_task_id" ON "workflow_step_tasks" ("task_id");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_workflow_template_version" ON "workflow_template_versions" ("template_key", "version");
CREATE INDEX IF NOT EXISTS "idx_schema_migrations_applied_at" ON "schema_migrations" ("applied_at");
CREATE UNIQUE INDEX IF NOT EXISTS "idx_project_asset_candidates_pending_identity" ON "project_asset_candidates" ("project_id", "category", "name_key") WHERE status = 'pending_confirmation' AND name_key <> '';
CREATE UNIQUE INDEX IF NOT EXISTS "idx_logical_model_source_active" ON "logical_models" (source_channel_model_id) WHERE source_channel_model_id <> '' AND archived_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "idx_users_email_nonempty" ON "users" (lower(email)) WHERE email <> '';
