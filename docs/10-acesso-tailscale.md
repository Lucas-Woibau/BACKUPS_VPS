# Acesso remoto pelo Tailscale

Painel em `https://vps-backup.<seu-tailnet>.ts.net`, acessível **somente** por aparelhos logados na sua conta
Tailscale. Nada é exposto na Internet: nenhuma porta nova na VPS, sem IP no link, HTTPS com certificado automático.

```
Seu PC / celular (app Tailscale) ──WireGuard──► container vps_backup_tailscale (userspace, sem privilégios)
                                                   │ tailscale serve (HTTPS) + header Tailscale-User-Login
                                                   ▼ rede interna ts_net (10.213.77.0/29)
                                                vps_backup_manager:8081
                                                   ├ só aceita o IP do sidecar (10.213.77.3)
                                                   ├ só aceita o login lucaswoibau7@gmail.com
                                                   └ login do painel + senha para mudanças sensíveis
```

## 1. Painel do Tailscale (uma vez) — https://login.tailscale.com/admin

### 1.1 DNS e HTTPS
**DNS** → ative **MagicDNS** e, mais abaixo, **HTTPS Certificates**. Anote o nome do tailnet (ex.: `tail1234ab.ts.net`).

> O nome `vps-backup.<tailnet>.ts.net` aparece nos logs públicos de certificados (Certificate Transparency).
> Isso revela só o nome — o acesso continua restrito ao seu tailnet.

### 1.2 Política de acesso (ACL)
**Access controls** → edite a política. Ela precisa conter a tag e as duas regras abaixo. Se você já tem regras
próprias, **mescle** (não apague o que usa hoje). A regra padrão `"src": ["*"], "dst": ["*"]` deve sair: é ela que
deixaria a VPS alcançar seus aparelhos.

```jsonc
{
  "tagOwners": {
    "tag:vps-backup": ["autogroup:admin"]
  },
  "grants": [
    // seus aparelhos falam entre si (comportamento de hoje)
    { "src": ["autogroup:member"], "dst": ["autogroup:self"], "ip": ["*"] },
    // seus aparelhos -> painel da VPS, somente HTTPS
    { "src": ["lucaswoibau7@gmail.com"], "dst": ["tag:vps-backup"], "ip": ["tcp:443"] }
    // NENHUMA regra com "src": ["tag:vps-backup"]: a VPS não inicia conexão com nada do seu tailnet.
  ],
  "ssh": [
    { "action": "check", "src": ["autogroup:member"], "dst": ["autogroup:self"], "users": ["autogroup:nonroot", "root"] }
  ],
  "tests": [
    { "src": "lucaswoibau7@gmail.com", "accept": ["tag:vps-backup:443"], "deny": ["tag:vps-backup:22"] },
    { "src": "tag:vps-backup", "deny": ["lucaswoibau7@gmail.com:22", "lucaswoibau7@gmail.com:443"] }
  ]
}
```

Os `tests` fazem o Tailscale **recusar salvar** a política se ela permitir a VPS alcançar seus aparelhos.

### 1.3 Chave de autenticação
**Settings → Keys → Generate auth key**:

| Opção | Valor |
|---|---|
| Reusable | **desligado** (uso único) |
| Expiration | 1 dia |
| Ephemeral | **desligado** (o nó precisa persistir) |
| Pre-approved | ligado |
| Tags | `tag:vps-backup` |

Nós com tag não expiram; depois do primeiro registro a chave não é mais usada.

## 2. GitHub (uma vez)
**Settings → Secrets and variables → Actions → New repository secret**: `TS_AUTHKEY` = a chave gerada.

Faça push (ou *Actions → Build & Deploy → Run workflow* na `main`). O deploy:
- copia `deploy/tailscale/serve.json` para a VPS;
- passa `TS_AUTHKEY` só pela sessão SSH (não grava em disco) e sobe `app` + `tailscale`;
- nas próximas execuções, o sidecar continua subindo porque o volume `tailscale_state` já existe.

Depois que o nó `vps-backup` aparecer em **Machines**, você pode apagar o secret `TS_AUTHKEY` (opcional; a chave é
de uso único e já foi consumida).

## 3. Seus aparelhos
Instale o Tailscale (PC já tem; celular: App Store / Play Store), entre com `lucaswoibau7@gmail.com` e abra
`https://vps-backup.<seu-tailnet>.ts.net`. O primeiro acesso pode levar alguns segundos (emissão do certificado).

## 4. Validação
- [ ] Painel abre no PC e no celular pelo endereço `.ts.net`.
- [ ] Com o Tailscale desligado, o endereço não abre.
- [ ] Em **Machines → vps-backup**, a tag `tag:vps-backup` aparece e "Key expiry" está desativado.
- [ ] **Access controls → Preview rules** para `tag:vps-backup`: nenhum destino permitido.
- [ ] Logs do painel não mostram `Tailnet: requisição recusada` para os seus acessos.

## Segurança — o que garante o quê

| Ameaça | Proteção |
|---|---|
| Scanner/ataque vindo da Internet | Não há porta exposta; só o tailnet alcança o sidecar. |
| Outro usuário/aparelho no tailnet | ACL só libera `lucaswoibau7@gmail.com` → `tcp:443`; o app confere `Tailscale-User-Login`. |
| Container vizinho (MySeeds/Bibliotrack) forjando o header | Porta 8081 só aceita o IP fixo do sidecar na rede `ts_net` (internal), onde nenhum outro container entra. |
| VPS invadida usando o Tailscale para atacar seu PC | ACL sem regra com origem `tag:vps-backup` (+ `tests` que impedem regressão). |
| Sidecar comprometido | Roda sem capabilities, `read_only`, `no-new-privileges`, modo userspace (sem `/dev/net/tun`). |
| Configuração incompleta | A porta 8081 recusa tudo (fail closed). |

Configuração no app (em `docker-compose.prod.yml`): `TAILNET_PORT=8081`, `TAILNET_PROXY_IP=10.213.77.3`,
`TAILNET_ALLOWED_LOGINS=lucaswoibau7@gmail.com` (vários: separados por vírgula).

## Problemas comuns
- **Sidecar não sobe / pede login**: o secret `TS_AUTHKEY` não existia no primeiro deploy, ou a chave expirou. Gere
  outra e rode o workflow de novo.
- **Erro de certificado**: HTTPS Certificates desligado no painel do Tailscale (passo 1.1).
- **403 "Acesso negado"**: você está logado no Tailscale com outra conta, ou abriu pelo IP `100.x` em vez do nome
  `.ts.net`.
- **Rede `ts_net` em conflito** ("Pool overlaps"): outra rede Docker usa `10.213.77.0/29`. Troque a subnet e os dois
  `ipv4_address` em `docker-compose.prod.yml`, o IP em `deploy/tailscale/serve.json` e `TAILNET_PROXY_IP`.
