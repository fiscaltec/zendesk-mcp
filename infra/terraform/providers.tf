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

  # No backend, and no state: this configuration is a RECORD of the estate, never applied (decided
  # 2026-10-28, as the Vitally MCP estate keeps its own). The estate is built and changed with az CLI
  # (docs/runbooks/build-the-estate.md), and this file set is updated by hand to match. `init
  # -backend=false` and `validate` are the only commands to run against it.
  #
  # Adopting it later — making Terraform the source of truth — would mean a remote backend outside
  # zendesk-prod-rg-uksouth and an import block per resource, read through a plan line by line, as
  # vitally-mcp's infra/terraform/README.md "Adoption" describes. It is a deliberate step, not a routine.
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {
    key_vault {
      # Never auto-purge: the vault has purge protection on.
      purge_soft_delete_on_destroy = false
    }
  }
}

# Entra ID / Microsoft Graph. Pinned explicitly so a mis-set `az login` context cannot silently
# target another directory.
provider "azuread" {
  tenant_id = var.tenant_id
}
