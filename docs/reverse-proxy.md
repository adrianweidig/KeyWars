# Reverse Proxy

KeyWars terminiert TLS nicht selbst. Der empfohlene Aufbau ist:

```text
Browser → HTTPS-Proxy → http://127.0.0.1:8080
```

Der Proxy muss Host und Schema weiterreichen sowie WebSockets für
`/hubs/arena` zulassen. Beispiel für einen vorhandenen Nginx-TLS-vHost:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 3600s;
    proxy_buffering off;
    client_max_body_size 256k;
}
```

Außerdem HTTP am Proxy nach HTTPS umleiten. Läuft der Proxy nicht über
Loopback, dessen exakte Adresse oder ein enges Netz über
`KEYWARS_PROXY_KNOWN_PROXIES` beziehungsweise
`KEYWARS_PROXY_KNOWN_NETWORKS` vertrauen. KeyWars wertet nur einen Proxy-Hop
aus; breite interne Netze sind unnötig.

Nach der Änderung den installationsspezifischen öffentlichen HTTPS-Pfad
`/health/ready` prüfen. Zusätzlich Login und eine Live-Arena im Browser testen.
KeyWars erzeugt relative Links, setzt nach erkanntem HTTPS HSTS und führt selbst
keine HTTP-zu-HTTPS-Weiterleitung aus.
