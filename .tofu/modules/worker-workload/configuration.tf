locals {
  vault_references = {
    rabbitmq  = var.vault_references.rabbitmq
    new_relic = var.vault_references.new_relic
  }

  non_secret_environment = merge(
    {
      HTTP_PORT                                                     = "8080"
      ASPNETCORE_ENVIRONMENT                                        = var.runtime_configuration.aspnetcore_environment
      ASPNETCORE_HTTP_PORTS                                         = "8080"
      AZURE_CLIENT_ID                                               = var.runtime_identity.client_id
      Database__Host                                                = var.database.host
      Database__Port                                                = tostring(var.database.port)
      Database__Database                                            = var.database.name
      Database__Username                                            = var.database.username
      Database__UseEntraAuth                                        = "true"
      Database__MaxPoolSize                                         = tostring(var.database.max_pool_size)
      Database__ManagedIdentityClientId                             = var.runtime_identity.client_id
      MessageBridge__Topology__EnvironmentPrefix                    = var.runtime_configuration.topology_prefix
      MessageBridge__Topology__Durable                              = tostring(var.runtime_configuration.topology_durable)
      MessageBridge__TransportRetry__ImmediateRetryCount            = tostring(var.runtime_configuration.immediate_retry_count)
      MessageBridge__ProcessingHistory__RecoveryEnabled             = tostring(var.runtime_configuration.recovery_enabled)
      MessageBridge__ProcessingHistory__StaleThresholdMinutes       = tostring(var.runtime_configuration.stale_threshold_minutes)
      MessageBridge__ProcessingHistory__CleanupEnabled              = tostring(var.runtime_configuration.cleanup_enabled)
      MessageBridge__ProcessingHistory__CleanupBatchSize            = tostring(var.runtime_configuration.cleanup_batch_size)
      MessageBridge__ProcessingHistory__CleanupIntervalMilliseconds = tostring(var.runtime_configuration.cleanup_interval_ms)
      MessageBridge__Tenants__AllowedTenantIds                      = join(",", sort(tolist(var.runtime_configuration.allowed_tenant_ids)))
      MessageBridge__RateLimiting__WhatsAppPermitsPerWindow         = tostring(var.runtime_configuration.whatsapp_rate_limit)
      MessageBridge__RateLimiting__EmailPermitsPerWindow            = tostring(var.runtime_configuration.email_rate_limit)
      MessageBridge__RateLimiting__WindowSizeSeconds                = tostring(var.runtime_configuration.rate_limit_window_secs)
      Observability__OtlpEndpoint                                   = var.runtime_configuration.otlp_endpoint
      Observability__ServiceName                                    = var.runtime_configuration.otlp_service_name
      Observability__MetricsEndpointEnabled                         = "false"
      OTEL_EXPORTER_OTLP_ENDPOINT                                   = var.runtime_configuration.otlp_endpoint
      OTEL_SERVICE_NAME                                             = var.runtime_configuration.otlp_service_name
    },
    {
      for index, interval in var.runtime_configuration.delayed_redelivery :
      "MessageBridge__TransportRetry__DelayedRedeliveryIntervals__${index}" => interval
    },
    {
      "MessageBridge__ProcessingHistory__${var.runtime_configuration.aspnetcore_environment}RetentionHours" = tostring(var.runtime_configuration.retention_hours)
    },
  )

  secret_environment = {
    RabbitMq__ConnectionString = var.vault_references.rabbitmq.name
    OTEL_EXPORTER_OTLP_HEADERS = var.vault_references.new_relic.name
  }

  # The migration job runs the EF bundle only. It receives Entra-authenticated database coordinates
  # for the migrator identity and nothing from the worker's secret, transport, or telemetry surface.
  migration_environment = {
    AZURE_CLIENT_ID                   = var.migrator_identity.client_id
    Database__Host                    = var.migration_database.host
    Database__Port                    = tostring(var.migration_database.port)
    Database__Database                = var.migration_database.name
    Database__Username                = var.migration_database.username
    Database__UseEntraAuth            = "true"
    Database__MaxPoolSize             = tostring(var.migration_database.max_pool_size)
    Database__ManagedIdentityClientId = var.migrator_identity.client_id
  }
}
