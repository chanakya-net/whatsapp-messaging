variable "alert_email" {
  description = "Non-secret bootstrap email address receiving Azure-native platform alerts."
  type        = string

  validation {
    condition     = can(regex("^[^@[:space:]]+@[^@[:space:]]+\\.[^@[:space:]]+$", var.alert_email))
    error_message = "alert_email must be a valid non-blank email address."
  }
}
