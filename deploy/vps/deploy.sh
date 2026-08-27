#!/bin/bash
# Pull-based deploy: publish-images.yml pushes :latest to GHCR; this timer pulls and rolls
# only the containers whose image changed (compose up -d is a no-op otherwise). Pull-based
# because the box's SSH allowlist (rightly) does not admit GitHub Actions runner IPs.
set -euo pipefail
cd /root/nextaurora
before=$(docker compose -f docker-compose.services.yml images -q | sort | md5sum)
docker compose -f docker-compose.services.yml pull -q
docker compose -f docker-compose.services.yml up -d --remove-orphans >/dev/null
after=$(docker compose -f docker-compose.services.yml images -q | sort | md5sum)
[ "$before" != "$after" ] && { echo "$(date -Is) rolled: $(docker compose -f docker-compose.services.yml ps --format "{{.Service}}={{.Status}}" | tr "\n" " ")"; docker image prune -f >/dev/null; } || true
