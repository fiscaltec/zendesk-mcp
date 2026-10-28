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

  # LOCAL state, as the Vitally MCP estate keeps (decided 2026-10-28): `terraform.tfstate` is written
  # beside this file and is gitignored. It holds the Entra client secret and the Teams webhook URL in
  # clear (Terraform persists sensitive values), so the machine that applies must be FISCAL-managed
  # with an encrypted disk, the file must never be committed or shared, and whoever applies next
  # needs the current copy — losing it means re-importing every resource before the next plan.
  #
  # To move to remote state later, add a `backend "azurerm"` block here (key "zendesk-mcp.tfstate",
  # use_azuread_auth = true) and run `terraform init -migrate-state` against a storage account that
  # is NOT in zendesk-prod-rg-uksouth, which this configuration creates and could destroy.
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
