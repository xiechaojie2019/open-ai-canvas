-- 由 scripts/generate-entities.py 从 schema-dump.json 生成，请勿手工编辑。
-- 目标数据库：postgres
-- 列类型取自 GORM 的 postgres dialector DataTypeOf，
-- 保证与 Go 侧 AutoMigrate 产出的物理结构一致。

CREATE TABLE IF NOT EXISTS "admin_audit_events" (
    "id" varchar(36) NOT NULL,
    "actor_user_id" varchar(36),
    "action" varchar(80),
    "target_type" varchar(40),
    "target_id" varchar(160),
    "summary" varchar(500),
    "metadata_json" text,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "agent_profiles" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "scope" varchar(16),
    "project_id" varchar(36),
    "canvas_id" varchar(80),
    "content" text,
    "revision" bigint,
    "hash" varchar(64),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "announcement_image_drafts" (
    "resource_id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "created_at" timestamptz,
    PRIMARY KEY ("resource_id")
);

CREATE TABLE IF NOT EXISTS "announcements" (
    "id" varchar(36) NOT NULL,
    "title" varchar(120),
    "content" text,
    "image_resource_id" varchar(36),
    "level" varchar(24),
    "pinned" boolean,
    "status" varchar(24),
    "created_by" varchar(36),
    "published_at" timestamptz,
    "closed_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "api_call_logs" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "trace_id" varchar(96),
    "request_id" varchar(96),
    "channel_id" varchar(36),
    "task_id" varchar(36),
    "billing_order_id" varchar(36),
    "source" varchar(64),
    "capability" varchar(32),
    "operation" varchar(64),
    "request_kind" varchar(24),
    "billable" boolean,
    "api_format" varchar(24),
    "method" varchar(16),
    "path" text,
    "model" varchar(120),
    "status" varchar(24),
    "status_code" bigint,
    "duration_ms" bigint,
    "poll_count" bigint,
    "provider_status" varchar(32),
    "input_tokens" bigint,
    "output_tokens" bigint,
    "cached_tokens" bigint,
    "usage_available" boolean,
    "media_count" bigint,
    "video_seconds" bigint,
    "provider_request_id" varchar(160),
    "estimated_cost_micros" bigint,
    "cost_available" boolean,
    "currency" varchar(12),
    "error_code" varchar(80),
    "error" text,
    "concurrency_limit" bigint,
    "upstream_url" text,
    "request_content_type" varchar(160),
    "request_body" text,
    "response_body" text,
    "started_at" timestamptz,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "ark_private_asset_bindings" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "resource_id" varchar(36),
    "project_name" varchar(160),
    "asset_group_id" varchar(120),
    "ark_asset_id" varchar(120),
    "status" varchar(24),
    "error" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_folders" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "name" varchar(80),
    "name_key" varchar(80),
    "position" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_representations" (
    "id" varchar(36) NOT NULL,
    "task_id" varchar(36),
    "asset_version_id" varchar(36),
    "resource_id" varchar(36),
    "media_type" varchar(24),
    "role" varchar(32),
    "metadata_json" text,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "asset_versions" (
    "id" varchar(36) NOT NULL,
    "asset_id" varchar(80),
    "version" bigint,
    "status" varchar(24),
    "definition_json" text,
    "prompt" text,
    "note" varchar(500),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "assets" (
    "id" varchar(80) NOT NULL,
    "user_id" varchar(36),
    "folder_id" varchar(36),
    "kind" varchar(24),
    "category" varchar(32),
    "status" varchar(24),
    "primary_version_id" varchar(36),
    "title" varchar(240),
    "payload_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "auth_sessions" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "token_hash" text,
    "expires_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "billing_orders" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "idempotency_key" varchar(160),
    "task_id" varchar(36),
    "channel_id" varchar(36),
    "channel_model_id" varchar(36),
    "price_tier_id" varchar(36),
    "price_tier_version" bigint,
    "price_selector_json" text,
    "model" varchar(120),
    "capability" varchar(32),
    "scene" varchar(80),
    "billing_mode" varchar(32),
    "price_version" bigint,
    "unit_price_microcredits" bigint,
    "multiplier_basis_points" bigint,
    "quantity" bigint,
    "amount_microcredits" bigint,
    "reserved_amount_microcredits" bigint,
    "charge_limit_microcredits" bigint,
    "actual_amount_microcredits" bigint,
    "refunded_amount_microcredits" bigint,
    "input_token_price_microcredits" bigint,
    "output_token_price_microcredits" bigint,
    "cached_token_price_microcredits" bigint,
    "input_tokens" bigint,
    "output_tokens" bigint,
    "cached_tokens" bigint,
    "usage_available" boolean,
    "status" varchar(24),
    "provider_request_id" varchar(160),
    "error" varchar(1000),
    "resolved_by" varchar(36),
    "resolution_note" varchar(500),
    "started_at" timestamptz,
    "settled_at" timestamptz,
    "refunded_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_projects" (
    "id" varchar(80) NOT NULL,
    "user_id" varchar(36),
    "project_id" varchar(36),
    "title" varchar(240),
    "payload_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_shares" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "project_id" varchar(80),
    "token_hash" varchar(64),
    "token_cipher" text,
    "enabled" boolean,
    "expires_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "canvas_unit_links" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "canvas_id" varchar(80),
    "unit_id" varchar(36),
    "role" varchar(32),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "channel_model_price_tiers" (
    "id" varchar(36) NOT NULL,
    "channel_model_id" varchar(36),
    "selector_key" varchar(500) NOT NULL DEFAULT '{}',
    "selector_json" text NOT NULL DEFAULT '{}',
    "resolution" varchar(24) NOT NULL DEFAULT '*',
    "video_seconds" bigint NOT NULL DEFAULT 0,
    "provider_model_key" varchar(120),
    "billing_mode" varchar(32),
    "unit_price_microcredits" bigint,
    "input_token_price_microcredits" bigint,
    "output_token_price_microcredits" bigint,
    "cached_token_price_microcredits" bigint,
    "price_configured" boolean,
    "enabled" boolean,
    "price_version" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    "deleted_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "channel_models" (
    "id" varchar(36) NOT NULL,
    "channel_id" varchar(36),
    "model_key" varchar(120),
    "provider_model_key" varchar(120),
    "display_name" varchar(160),
    "sort_order" bigint NOT NULL DEFAULT 0,
    "icon" varchar(80),
    "capability" varchar(32),
    "protocol" varchar(32),
    "billing_mode" varchar(32),
    "unit_price_microcredits" bigint,
    "input_token_price_microcredits" bigint,
    "output_token_price_microcredits" bigint,
    "cached_token_price_microcredits" bigint,
    "price_configured" boolean,
    "enabled" boolean,
    "price_version" bigint,
    "capability_config_json" text,
    "capability_version" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    "deleted_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "character_voice_bindings" (
    "id" varchar(36) NOT NULL,
    "asset_version_id" varchar(36),
    "voice_profile_id" varchar(36),
    "instructions" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "cloud_agent_canvas_mutations" (
    "id" varchar(80) NOT NULL,
    "run_id" varchar(80),
    "user_id" varchar(36),
    "canvas_id" varchar(80),
    "step_id" varchar(160),
    "operation" varchar(64),
    "before_snapshot_hash" varchar(64),
    "after_snapshot_hash" varchar(64),
    "before_json" text,
    "has_submitted_task" boolean,
    "status" varchar(24),
    "created_at" timestamptz,
    "undone_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "cloud_agent_executions" (
    "id" varchar(80) NOT NULL,
    "user_id" varchar(36),
    "status" varchar(32),
    "revision" bigint,
    "canvas_id" varchar(80),
    "active_task_id" varchar(80),
    "media_task_id" varchar(80),
    "cleanup_pending" boolean NOT NULL DEFAULT false,
    "failure_message" varchar(1000),
    "state_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "creation_runs" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "client_key" varchar(120),
    "create_hash" varchar(64),
    "canvas_id" varchar(80),
    "revision" bigint,
    "execution_epoch" bigint,
    "execution_owner" varchar(120),
    "lease_expires_at" timestamptz,
    "status" varchar(32),
    "state_json" text,
    "approved_proposal_version" bigint,
    "approved_proposal_hash" varchar(64),
    "approved_operations_json" text,
    "approved_canvas_json" text,
    "approved_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "creation_submissions" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "run_id" varchar(36),
    "item_key" varchar(160),
    "proposal_version" bigint,
    "proposal_hash" varchar(64),
    "request_json" text,
    "request_hash" varchar(64),
    "quote_json" text,
    "price_signature" text,
    "expires_at" timestamptz,
    "approved_at" timestamptz,
    "revoked_at" timestamptz,
    "task_id" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "credit_accounts" (
    "user_id" varchar(36) NOT NULL,
    "available_microcredits" bigint,
    "reserved_microcredits" bigint,
    "version" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("user_id")
);

CREATE TABLE IF NOT EXISTS "credit_ledger_entries" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "type" varchar(32),
    "amount_microcredits" bigint,
    "available_delta_microcredits" bigint,
    "reserved_delta_microcredits" bigint,
    "available_after_microcredits" bigint,
    "reserved_after_microcredits" bigint,
    "billing_order_id" varchar(36),
    "payment_order_id" varchar(36),
    "redeem_code_id" varchar(36),
    "actor_user_id" varchar(36),
    "model" varchar(120),
    "channel_id" varchar(36),
    "scene" varchar(80),
    "note" varchar(500),
    "reference_key" varchar(180),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "email_verification_codes" (
    "id" varchar(36) NOT NULL,
    "email" varchar(160),
    "code_hash" varchar(64),
    "purpose" varchar(32),
    "expires_at" timestamptz,
    "used_at" timestamptz,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "id_sequences" (
    "name" varchar(40) NOT NULL,
    "value" bigint,
    "updated_at" timestamptz,
    PRIMARY KEY ("name")
);

CREATE TABLE IF NOT EXISTS "logical_model_revisions" (
    "id" varchar(36) NOT NULL,
    "logical_model_id" varchar(36),
    "version" bigint,
    "capability_spec_json" text,
    "default_options_json" text,
    "created_by" varchar(36),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "logical_model_routes" (
    "id" varchar(36) NOT NULL,
    "logical_model_revision_id" varchar(36),
    "channel_model_id" varchar(36),
    "enabled" boolean,
    "priority" bigint,
    "weight" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "logical_models" (
    "id" varchar(36) NOT NULL,
    "code" varchar(80),
    "name" varchar(120),
    "icon" varchar(80),
    "description" varchar(500),
    "capability" varchar(32),
    "enabled" boolean,
    "sort_order" bigint,
    "revision_sequence" bigint NOT NULL DEFAULT 0,
    "active_revision_id" varchar(36),
    "source_channel_model_id" varchar(36),
    "price_policy" varchar(24) DEFAULT 'unified',
    "billing_mode" varchar(32),
    "unit_price_microcredits" bigint,
    "input_price_microcredits" bigint,
    "output_price_microcredits" bigint,
    "cached_price_microcredits" bigint,
    "legacy_model_ids_json" text,
    "archived_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "model_channels" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "scope" varchar(24),
    "enabled" boolean,
    "name" varchar(80),
    "public_alias" varchar(80) NOT NULL DEFAULT '',
    "sort_order" bigint NOT NULL DEFAULT 0,
    "base_url" text,
    "api_key" text,
    "secret_key" text,
    "api_format" varchar(24),
    "concurrency_limit" bigint,
    "models_json" text,
    "retired_models_json" text,
    "headers_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    "deleted_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "model_pricings" (
    "id" varchar(36) NOT NULL,
    "channel_id" varchar(36),
    "model" varchar(120),
    "capability" varchar(32),
    "currency" varchar(12),
    "input_per_million_micros" bigint,
    "output_per_million_micros" bigint,
    "cached_per_million_micros" bigint,
    "per_request_micros" bigint,
    "per_media_micros" bigint,
    "per_video_second_micros" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "o_auth_states" (
    "id" varchar(36) NOT NULL,
    "provider" varchar(32),
    "state_hash" varchar(64),
    "code_verifier" varchar(160),
    "next_path" text,
    "expires_at" timestamptz,
    "used_at" timestamptz,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_notifications" (
    "id" varchar(36) NOT NULL,
    "provider_id" varchar(80),
    "provider_event_id" varchar(160),
    "provider_config_id" varchar(36),
    "merchant_order_no" varchar(32),
    "payment_order_id" varchar(36),
    "payload_digest" varchar(64),
    "payload_cipher" text,
    "normalized_json" text,
    "status" varchar(24),
    "attempts" bigint,
    "last_error" varchar(1000),
    "next_attempt_at" timestamptz,
    "processed_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_orders" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "idempotency_key" varchar(120),
    "merchant_order_no" varchar(32),
    "product_id" varchar(36),
    "product_name" varchar(120),
    "provider_id" varchar(80),
    "plugin_id" varchar(120),
    "plugin_version" varchar(40),
    "provider_config_id" varchar(36),
    "provider_config_version" bigint,
    "amount_fen" bigint,
    "currency" varchar(8),
    "credits_microcredits" bigint,
    "status" varchar(24),
    "provider_trade_no" varchar(96),
    "provider_status" varchar(40),
    "checkout_mode" varchar(24),
    "checkout_value" text,
    "checkout_expires_at" timestamptz,
    "expires_at" timestamptz,
    "provider_paid_at" timestamptz,
    "credited_at" timestamptz,
    "closed_at" timestamptz,
    "last_queried_at" timestamptz,
    "last_error" varchar(1000),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_provider_configs" (
    "id" varchar(36) NOT NULL,
    "provider_id" varchar(80),
    "plugin_id" varchar(120),
    "plugin_version" varchar(40),
    "version" bigint,
    "enabled" boolean,
    "close_after_minutes" bigint,
    "config_cipher" text,
    "config_digest" varchar(64),
    "created_by" varchar(36),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_reconciliation_items" (
    "id" varchar(36) NOT NULL,
    "run_id" varchar(36),
    "provider_id" varchar(80),
    "payment_order_id" varchar(36),
    "merchant_order_no" varchar(32),
    "provider_trade_no" varchar(96),
    "amount_fen" bigint,
    "currency" varchar(8),
    "result" varchar(48),
    "resolved" boolean,
    "detail" varchar(1000),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "payment_reconciliation_runs" (
    "id" varchar(36) NOT NULL,
    "provider_id" varchar(80),
    "config_id" varchar(36),
    "bill_date" varchar(10),
    "status" varchar(24),
    "total_items" bigint,
    "match_items" bigint,
    "recovered_items" bigint,
    "error_items" bigint,
    "error" varchar(1000),
    "started_by" varchar(36),
    "started_at" timestamptz,
    "completed_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "plugin_platform_states" (
    "plugin_id" varchar(120) NOT NULL,
    "available" boolean,
    "updated_by" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("plugin_id")
);

CREATE TABLE IF NOT EXISTS "production_task_links" (
    "id" varchar(36) NOT NULL,
    "task_id" varchar(36),
    "project_id" varchar(36),
    "canvas_id" varchar(80),
    "unit_id" varchar(36),
    "shot_id" varchar(36),
    "workflow_step_id" varchar(36),
    "artifact_type" varchar(40),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_candidates" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "unit_id" varchar(36),
    "shot_id" varchar(36),
    "name" varchar(240),
    "name_key" varchar(240),
    "category" varchar(32),
    "status" varchar(32),
    "source" varchar(48),
    "details_json" text,
    "resolved_asset_id" varchar(80),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_folders" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "parent_id" varchar(36),
    "name" varchar(240),
    "name_key" varchar(240),
    "style" varchar(24),
    "theme" varchar(24),
    "position" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_asset_links" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "asset_id" varchar(80),
    "folder_id" varchar(36),
    "position" bigint,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "project_units" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "parent_id" varchar(36),
    "kind" varchar(24),
    "title" varchar(240),
    "source_text" text,
    "word_count" bigint,
    "status" varchar(24),
    "position" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "projects" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "name" varchar(240),
    "type" varchar(32),
    "aspect_ratio" varchar(16),
    "source_type" varchar(32),
    "description" text,
    "cover_resource_id" varchar(36),
    "style_preset_id" varchar(64),
    "style_profile_json" text,
    "default_image_model" varchar(500),
    "default_video_model" varchar(500),
    "status" varchar(24),
    "revision" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "prompt_templates" (
    "id" varchar(36) NOT NULL,
    "operation" varchar(64),
    "name" varchar(120),
    "version" bigint,
    "content" text,
    "output_type" varchar(24),
    "enabled" boolean,
    "created_by" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "redeem_batches" (
    "id" varchar(36) NOT NULL,
    "amount_microcredits" bigint,
    "count" bigint,
    "note" varchar(500),
    "created_by" varchar(36),
    "codes_cipher" text,
    "expires_at" timestamptz,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "redeem_codes" (
    "id" varchar(36) NOT NULL,
    "batch_id" varchar(36),
    "code_hash" varchar(64),
    "code_suffix" varchar(4),
    "amount_microcredits" bigint,
    "status" varchar(24),
    "redeemed_by" varchar(36),
    "redeemed_at" timestamptz,
    "redeemed_ip" varchar(64),
    "expires_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "resource_deletion_jobs" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "resource_id" varchar(36),
    "provider" varchar(24),
    "endpoint" text,
    "bucket" varchar(160),
    "storage_setting_id" varchar(36),
    "object_key" text,
    "status" varchar(24),
    "attempts" bigint,
    "last_error" text,
    "next_attempt_at" timestamptz,
    "lease_owner" varchar(120),
    "lease_expires_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "resources" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "kind" varchar(24),
    "status" varchar(24),
    "provider" varchar(24),
    "endpoint" text,
    "bucket" varchar(160),
    "storage_setting_id" varchar(36),
    "object_key" text,
    "public_url" text,
    "mime_type" varchar(120),
    "size" bigint,
    "width" bigint,
    "height" bigint,
    "duration_ms" bigint,
    "e_tag" varchar(160),
    "playback_status" varchar(24),
    "playback_object_key" text,
    "playback_error" text,
    "upload_key" varchar(64),
    "error" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "results" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "task_id" varchar(36),
    "kind" varchar(64),
    "url" text,
    "payload" text,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "route_attempts" (
    "id" varchar(36) NOT NULL,
    "task_id" varchar(36),
    "route_run" bigint,
    "attempt_number" bigint,
    "logical_model_id" varchar(36),
    "logical_model_revision_id" varchar(36),
    "route_id" varchar(36),
    "channel_model_id" varchar(36),
    "channel_id" varchar(36),
    "status" varchar(32),
    "dispatch_state" varchar(32),
    "provider_request_id" varchar(160),
    "failure_code" varchar(80),
    "failure_message" varchar(1000),
    "started_at" timestamptz,
    "completed_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "schema_migrations" (
    "version" bigserial NOT NULL,
    "name" varchar(160) NOT NULL,
    "checksum" varchar(96) NOT NULL,
    "applied_at" timestamptz NOT NULL,
    PRIMARY KEY ("version")
);

CREATE TABLE IF NOT EXISTS "shot_artifacts" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "unit_id" varchar(36),
    "shot_id" varchar(36),
    "revision_id" varchar(36),
    "task_id" varchar(36),
    "type" varchar(40),
    "version" bigint,
    "resource_id" varchar(36),
    "status" varchar(24),
    "selected" boolean,
    "metadata_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shot_asset_references" (
    "id" varchar(36) NOT NULL,
    "shot_id" varchar(36),
    "asset_version_id" varchar(36),
    "role" varchar(32),
    "status" varchar(24),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shot_revisions" (
    "id" varchar(36) NOT NULL,
    "shot_id" varchar(36),
    "version" bigint,
    "plot_description" text,
    "action" text,
    "dialogue" text,
    "shot_size" varchar(80),
    "camera_angle" varchar(80),
    "camera_movement" varchar(120),
    "duration_ms" bigint,
    "image_prompt" text,
    "video_prompt" text,
    "negative_prompt" text,
    "continuity_notes" text,
    "action_beats_json" text,
    "created_by" varchar(36),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "shots" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "unit_id" varchar(36),
    "current_revision_id" varchar(36),
    "title" varchar(240),
    "description" text,
    "position" bigint,
    "duration_ms" bigint,
    "status" varchar(24),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skill_files" (
    "id" varchar(36) NOT NULL,
    "skill_version_id" varchar(36),
    "path" varchar(1000),
    "kind" varchar(24),
    "mime_type" varchar(255),
    "size" bigint,
    "sha256" varchar(64),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skill_versions" (
    "id" varchar(36) NOT NULL,
    "skill_id" varchar(36),
    "version_label" varchar(64),
    "content_hash" varchar(64),
    "entry_path" varchar(1000),
    "package_key" varchar(1000),
    "file_count" bigint,
    "total_bytes" bigint,
    "source_commit" varchar(64),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "skills" (
    "id" varchar(36) NOT NULL,
    "owner_id" varchar(36),
    "author_name" varchar(120),
    "author_avatar_url" varchar(1000),
    "name" varchar(80),
    "description" varchar(500),
    "instruction" text,
    "current_version_id" varchar(36),
    "version_label" varchar(64),
    "content_hash" varchar(64),
    "file_count" bigint,
    "total_bytes" bigint,
    "source_type" varchar(24),
    "source_url" varchar(1000),
    "source_ref" varchar(255),
    "source_subdir" varchar(1000),
    "source_commit" varchar(64),
    "sync_status" varchar(24),
    "sync_error" text,
    "auto_update" boolean,
    "last_checked_at" timestamptz,
    "last_synced_at" timestamptz,
    "status" bigint,
    "source" bigint,
    "tag" varchar(32),
    "sort_weight" bigint,
    "is_private" boolean,
    "markdown_url" varchar(500),
    "showcase_media_json" text,
    "extra_info" text,
    "initial_like_count" bigint,
    "initial_added_count" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "storage_locations" (
    "id" varchar(36) NOT NULL,
    "scope" varchar(16),
    "owner_id" varchar(36),
    "provider" varchar(24),
    "location_digest" varchar(64),
    "value_json" text,
    "tested_digest" varchar(64),
    "tested_at" timestamptz,
    "active" boolean,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "style_profiles" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "name" varchar(160),
    "description" varchar(500),
    "cover_url" text,
    "tags_json" text,
    "profile_json" text,
    "favorite" boolean,
    "last_used_at" timestamptz,
    "revision" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "system_settings" (
    "key" varchar(80) NOT NULL,
    "value_json" text,
    "updated_by" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("key")
);

CREATE TABLE IF NOT EXISTS "task_logs" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "task_id" varchar(36),
    "trace_id" varchar(96),
    "request_id" varchar(96),
    "level" varchar(24),
    "message" text,
    "payload" text,
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "task_text_delta" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "task_id" varchar(36),
    "sequence" bigint,
    "content" text,
    "byte_count" bigint,
    "created_at" timestamptz,
    "expires_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "tasks" (
    "creation_submission_id" varchar(36),
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "trace_id" varchar(96),
    "request_id" varchar(96),
    "project_id" varchar(80),
    "type" varchar(64),
    "status" varchar(24),
    "stage" varchar(80),
    "progress" bigint,
    "prompt" text,
    "operation" varchar(64),
    "provider" varchar(64),
    "model" varchar(120),
    "logical_model_id" varchar(36),
    "logical_model_revision_id" varchar(36),
    "route_id" varchar(36),
    "channel_model_id" varchar(36),
    "route_run" bigint,
    "billing_order_id" varchar(36),
    "provider_request_id" varchar(160),
    "provider_cancel_status" varchar(24),
    "provider_cancel_error" text,
    "provider_cancel_attempts" bigint,
    "provider_cancel_requested_at" timestamptz,
    "provider_cancelled_at" timestamptz,
    "provider_cancel_next_check_at" timestamptz,
    "poll_stage" varchar(32),
    "next_poll_at" timestamptz,
    "lease_owner" varchar(120),
    "lease_expires_at" timestamptz,
    "input_json" text,
    "result_json" text,
    "text_draft" text,
    "error" text,
    "attempts" bigint,
    "started_at" timestamptz,
    "completed_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "topup_products" (
    "id" varchar(36) NOT NULL,
    "name" varchar(120),
    "description" varchar(500),
    "amount_fen" bigint,
    "credits_microcredits" bigint,
    "enabled" boolean,
    "sort_order" bigint,
    "created_by" varchar(36),
    "updated_by" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_announcement_reads" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "announcement_id" varchar(36),
    "read_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_daily_activities" (
    "id" varchar(64) NOT NULL,
    "day" date,
    "user_id" varchar(36),
    "first_active_at" timestamptz,
    "last_active_at" timestamptz,
    "login_count" bigint,
    "task_count" bigint,
    "agent_message_count" bigint,
    "canvas_active" boolean,
    "asset_count" bigint,
    "resource_count" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_daily_upload_usages" (
    "id" varchar(64) NOT NULL,
    "user_id" varchar(36),
    "day" varchar(10),
    "bytes" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_identities" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "provider" varchar(32),
    "subject" varchar(160),
    "provider_username" varchar(160),
    "avatar_url" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_oss_settings" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "enabled" boolean,
    "value_json" text,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_plugin_states" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "plugin_id" varchar(120),
    "enabled" boolean,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_prompt_customizations" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "operation" varchar(64),
    "mode" varchar(24),
    "content" text,
    "base_template_id" varchar(36),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "user_skill_states" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "skill_id" varchar(36),
    "installed_version_id" varchar(36),
    "auto_update" boolean,
    "added" boolean,
    "liked" boolean,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "users" (
    "id" varchar(36) NOT NULL,
    "username" varchar(80),
    "email" varchar(160),
    "display_name" varchar(80),
    "role" varchar(24),
    "status" varchar(24),
    "password_hash" text,
    "last_login_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "voice_profiles" (
    "id" varchar(36) NOT NULL,
    "user_id" varchar(36),
    "name" varchar(160),
    "provider" varchar(48),
    "voice_key" varchar(160),
    "language" varchar(80),
    "timbre" varchar(240),
    "sample_resource_id" varchar(36),
    "compatible_models_json" text,
    "status" varchar(24),
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_instances" (
    "id" varchar(36) NOT NULL,
    "project_id" varchar(36),
    "unit_id" varchar(36),
    "template_version_id" varchar(36),
    "scope" varchar(24),
    "status" varchar(24),
    "revision" bigint,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_step_instances" (
    "id" varchar(36) NOT NULL,
    "workflow_instance_id" varchar(36),
    "step_key" varchar(80),
    "name" varchar(160),
    "position" bigint,
    "status" varchar(24),
    "input_json" text,
    "output_json" text,
    "error" text,
    "started_at" timestamptz,
    "completed_at" timestamptz,
    "created_at" timestamptz,
    "updated_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_step_tasks" (
    "id" varchar(36) NOT NULL,
    "workflow_step_id" varchar(36),
    "task_id" varchar(36),
    "created_at" timestamptz,
    PRIMARY KEY ("id")
);

CREATE TABLE IF NOT EXISTS "workflow_template_versions" (
    "id" varchar(36) NOT NULL,
    "template_key" varchar(80),
    "name" varchar(160),
    "version" bigint,
    "definition_json" text,
    "created_at" timestamptz,
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
