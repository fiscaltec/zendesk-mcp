"""Key Vault secret-expiry scanner — runs as a scheduled Container Apps Job.

Gets a managed-identity token from the Container Apps identity endpoint, lists Key Vault
secrets via the KV REST API, and posts an Adaptive Card to the Teams Workflows webhook for
any secret expiring within 30 days. No Azure CLI dependency (runs on python:3-slim).

Per-person Zendesk token secrets (tag kind=zendesk-user-token, written by the app as
zendesk-user-* / zendesk-staging-user-*) are SKIPPED. Their expiry is the refresh token's, and it
heals itself: when it lapses, the next tool call offers that person a connect link and they
reconnect. Alerting on them would bury the two secrets that need a human to rotate them —
zendesk-oauth-client-secret and entra-mcp-client-secret — under one line per user.
"""
import json, os, datetime, urllib.request, urllib.parse

USER_TOKEN_KIND = "zendesk-user-token"


def getj(url, headers):
    return json.load(urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30))


ie = os.environ["IDENTITY_ENDPOINT"]
ih = os.environ["IDENTITY_HEADER"]
cid = os.environ["MI_CLIENT_ID"]
vault = os.environ["VAULT"]

q = urllib.parse.urlencode({"resource": "https://vault.azure.net", "api-version": "2019-08-01", "client_id": cid})
tok = getj(ie + "?" + q, {"X-IDENTITY-HEADER": ih})["access_token"]
print("[scan] token acquired", flush=True)

# List secrets, following nextLink pagination so large vaults aren't truncated (one secret per
# connected person makes this vault large).
secrets = []
next_url = "https://%s.vault.azure.net/secrets?api-version=7.4" % vault
while next_url:
    page = getj(next_url, {"Authorization": "Bearer " + tok})
    secrets.extend(page.get("value", []))
    next_url = page.get("nextLink")
now = datetime.datetime.now(datetime.timezone.utc)
thr = now + datetime.timedelta(days=30)
near = []
skipped = 0
for s in secrets:
    if (s.get("tags") or {}).get("kind") == USER_TOKEN_KIND:
        skipped += 1
        continue
    a = s.get("attributes") or {}
    exp = a.get("exp")
    if a.get("enabled") and exp:
        d = datetime.datetime.fromtimestamp(exp, datetime.timezone.utc)
        if d <= thr:
            near.append((s["id"].rstrip("/").split("/")[-1], d.date().isoformat()))
# The count only — never the names, which embed each person's Entra object id.
print("[scan] skipped %d per-person Zendesk token secret(s)" % skipped, flush=True)
print("[scan] near-expiry:", near, flush=True)

if not near:
    print("[scan] none within 30 days")
    raise SystemExit(0)

lines = "\n\n".join("- %s expires %s" % (n, e) for n, e in near)
card = {
    "type": "message",
    "attachments": [{
        "contentType": "application/vnd.microsoft.card.adaptive",
        "content": {
            "type": "AdaptiveCard",
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "version": "1.5",
            "body": [
                {"type": "TextBlock", "size": "Medium", "weight": "Bolder", "text": "Key Vault secrets nearing expiry (<=30 days)"},
                {"type": "TextBlock", "wrap": True, "text": lines},
                {"type": "TextBlock", "wrap": True, "isSubtle": True, "text": "Vault: %s - rotate per the 180-day standard (docs/runbooks/entra-and-zendesk-oauth.md)." % vault},
            ],
        },
    }],
}
urllib.request.urlopen(
    urllib.request.Request(os.environ["TEAMS_WEBHOOK"], json.dumps(card).encode(), {"Content-Type": "application/json"}),
    timeout=30,
)
print("[scan] posted to Teams", flush=True)
