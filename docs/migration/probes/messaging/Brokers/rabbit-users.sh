#!/usr/bin/env bash
# Two RabbitMQ users: each may declare and write its own exchange and queues only (configure / write / read regexes).
set -euo pipefail
c="$1"
docker exec "$c" rabbitmqctl add_user artflow artflow >/dev/null 2>&1 || true
docker exec "$c" rabbitmqctl add_user billing billing >/dev/null 2>&1 || true
docker exec "$c" rabbitmqctl set_permissions -p / artflow '^(artflow|artflow\..*)$' '^(artflow|artflow\..*|amq\.default)$' '^(artflow\..*|billing)$' >/dev/null
docker exec "$c" rabbitmqctl set_permissions -p / billing '^(billing|billing\..*)$' '^(billing|billing\..*|amq\.default)$' '^(billing\..*|artflow)$' >/dev/null
