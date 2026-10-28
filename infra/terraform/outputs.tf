# Values the out-of-band steps in README.md need. The Entra outputs live in entra.tf.

output "resource_group_name" {
  value = azurerm_resource_group.rg.name
}

output "acr_name" {
  description = "The ACR — the repo-level GitHub variable ACR_NAME."
  value       = azurerm_container_registry.acr.name
}

output "key_vault_name" {
  description = "The secret vault, for `az keyvault secret set` (from inside the VNet or a temporarily allowed network)."
  value       = azurerm_key_vault.secret.name
}

output "managed_identity_client_id" {
  description = "The GitHub secret AZURE_CLIENT_ID (deploy identity), and the apps' AZURE_CLIENT_ID."
  value       = azurerm_user_assigned_identity.app.client_id
}

output "managed_identity_principal_id" {
  description = "Object id of the identity's service principal — the target of the Graph GroupMember.Read.All grant."
  value       = azurerm_user_assigned_identity.app.principal_id
}

output "production_app_name" {
  description = "Production Container App — the production GitHub environment's CONTAINER_APP."
  value       = azurerm_container_app.app.name
}

output "staging_app_name" {
  description = "Staging Container App — the staging GitHub environment's CONTAINER_APP."
  value       = azurerm_container_app.staging.name
}

output "production_default_fqdn" {
  description = "CNAME target for the production hostname."
  value       = azurerm_container_app.app.ingress[0].fqdn
}

output "staging_default_fqdn" {
  description = "CNAME target for the staging hostname."
  value       = azurerm_container_app.staging.ingress[0].fqdn
}

output "custom_domain_verification_id" {
  description = "Value of the asuid.<host> TXT record each custom hostname needs."
  value       = azurerm_container_app_environment.env.custom_domain_verification_id
}

output "nat_egress_ip" {
  description = "Static egress address of both apps and the scanner job."
  value       = azurerm_public_ip.nat.ip_address
}
