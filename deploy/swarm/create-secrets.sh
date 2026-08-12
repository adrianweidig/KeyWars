#!/usr/bin/env sh
set -eu

if [ ! -t 0 ]; then
    printf '%s\n' 'Interaktives Terminal erforderlich.' >&2
    exit 1
fi

if ! docker info --format '{{.Swarm.LocalNodeState}}' | grep -q '^active$'; then
    printf '%s\n' 'Dieser Docker-Knoten ist kein aktiver Swarm-Manager.' >&2
    exit 1
fi

for secret_name in \
    keywars-postgres-password \
    keywars-database-connection \
    keywars-redis-connection
do
    if docker secret inspect "$secret_name" >/dev/null 2>&1; then
        printf '%s\n' "Secret $secret_name existiert bereits; Abbruch ohne Änderung." >&2
        exit 1
    fi
done

created_secrets=''
tty_hidden=false
cleanup()
{
    exit_code=$?
    trap - 0 HUP INT TERM
    if [ "$tty_hidden" = true ]; then
        stty echo
        printf '\n'
    fi
    if [ "$exit_code" -ne 0 ] && [ -n "$created_secrets" ]; then
        for secret_name in $created_secrets; do
            docker secret rm "$secret_name" >/dev/null 2>&1 || true
        done
        printf '%s\n' 'Unvollständig erstellte Secrets wurden entfernt.' >&2
    fi
    unset postgres_password escaped_password database_connection redis_connection
    exit "$exit_code"
}
trap cleanup 0 HUP INT TERM

printf '%s' 'Neues PostgreSQL-Kennwort: '
stty -echo
tty_hidden=true
IFS= read -r postgres_password
stty echo
tty_hidden=false
printf '\n'

if [ -z "$postgres_password" ]; then
    printf '%s\n' 'Das Kennwort darf nicht leer sein.' >&2
    exit 1
fi

escaped_password="$(printf '%s' "$postgres_password" | sed 's/"/""/g')"
database_connection="Host=keywars-postgres;Port=5432;Database=keywars;Username=keywars;"
database_connection="${database_connection}Password=\"${escaped_password}\""
redis_connection="${KEYWARS_REDIS_CONNECTION:-keywars-redis:6379,abortConnect=false}"

printf '%s' "$postgres_password" | docker secret create keywars-postgres-password - >/dev/null
created_secrets='keywars-postgres-password'
printf '%s' "$database_connection" | docker secret create keywars-database-connection - >/dev/null
created_secrets="$created_secrets keywars-database-connection"
printf '%s' "$redis_connection" | docker secret create keywars-redis-connection - >/dev/null
created_secrets="$created_secrets keywars-redis-connection"

unset postgres_password database_connection redis_connection
printf '%s\n' 'Drei Swarm-Secrets wurden erstellt.'
