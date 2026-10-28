terraform {
  required_version = ">= 1.6"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }

    # Entra objects (entra.tf): the app registration, its service principal, the Gate 1 assignments
    # and the sg-zendesk-* tier groups. The azurerm provider cannot express any of them.
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }

  # Remote state, configured at init time (partial configuration) so no storage account name is
  # committed:
  #
  #   terraform init \
  #     -backend-config="resource_group_name=<state RG>" \
  #     -backend-config="storage_account_name=<state account>" \
  #     -backend-config="container_name=tfstate"
  #
  # The state account must NOT live in zendesk-prod-rg-uksouth: that group is created by this
  # configuration, so state kept inside it could not exist before the first apply and would be
  # destroyed with the estate. Where it lives is in README.md's "Decisions pending".
  #
  # State holds the Entra client secret and the Teams webhook URL in clear (Terraform persists
  # sensitive values), so the account needs encryption, no public blob access and tight RBAC.
  backend "azurerm" {
    key              = "zendesk-mcp.tfstate"
    use_azuread_auth = true
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {
    key_vault {
      # Never auto-purge: both vaults have purge protection on.
      purge_soft_delete_on_destroy = false
    }
  }
}

# Entra ID / Microsoft Graph. Pinned explicitly so a mis-set `az login` context cannot silently
# target another directory.
provider "azuread" {
  tenant_id = var.tenant_id
}
