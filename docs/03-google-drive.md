# Fase 7 — Google Drive com rclone

## Como funciona

- O **rclone** já vem dentro da imagem. A configuração fica em `./rclone/rclone.conf` (volume, `chmod 600`).
- A autorização OAuth gera um **refresh token** guardado nesse arquivo. O rclone renova o access token
  sozinho (a cada ~1 h) e regrava o arquivo — por isso o volume é gravável. Seu PC não participa depois da
  autorização inicial.
- O app executa `rclone copyto` (upload em chunks), confere tamanho + MD5 do arquivo no Drive e só então marca sucesso.

## Jeito simples (recomendado): só o Gmail, pelo painel

1. Abra o painel pelo túnel SSH (`ssh -L 8080:127.0.0.1:8080 root@IP_DA_VPS`).
2. **Configurações → Google Drive** → digite seu Gmail → **Conectar Google Drive**.
3. Abre uma aba do Google já com a sua conta: clique em **Permitir**.
4. O navegador vai para um endereço `http://127.0.0.1:53682/?state=...&code=...` que **não abre** — isso é esperado
   (esse endereço só existe dentro da VPS). Copie o endereço inteiro da barra e cole no painel → **Concluir conexão**.
5. O painel mostra "Google Drive conectado (seu@gmail.com)", cria a pasta `Backups` e faz um upload de teste.

Pronto: a partir daí o token é renovado sozinho e nada mais depende do seu PC.

> Por que não dá para usar só o e-mail, sem clicar em "Permitir"? O Google não permite que nenhum sistema acesse
> um Drive sem o consentimento do dono da conta. É um clique, uma única vez. A senha do Gmail **nunca** é digitada
> no painel — só na página oficial do Google.
>
> O acesso usa o app OAuth oficial do rclone com escopo `drive.file` (o sistema só enxerga os arquivos que ele mesmo criou).

Se preferir fazer pelo terminal (ou usar um Client ID próprio, recomendado para muitos backups diários), siga os passos abaixo.

## Jeito manual (terminal)

## 1. (Opcional) Criar seu próprio Client ID no Google Cloud

O client ID padrão do rclone é compartilhado e sofre limite de taxa. Com o seu:

1. https://console.cloud.google.com → crie um projeto (ex.: `vps-backups`).
2. **APIs e serviços → Biblioteca** → ative **Google Drive API**.
3. **Tela de consentimento OAuth** → tipo *Externo* → preencha nome/e-mail → adicione seu e-mail como usuário de teste.
4. **IMPORTANTE:** clique em **Publicar aplicativo** (status *Em produção*). Em modo *Teste* o Google
   **expira o refresh token em 7 dias** e os backups param de subir. Para uso pessoal não é preciso verificação
   (aparece um aviso "app não verificado" na autorização — aceite).
5. **Credenciais → Criar credenciais → ID do cliente OAuth → App para computador**. Anote *Client ID* e *Client secret*.

## 2. Autorizar no seu PC (uma única vez)

Instale o rclone no PC (https://rclone.org/downloads/) e rode:

```bash
rclone authorize "drive" "SEU_CLIENT_ID" "SEU_CLIENT_SECRET"
```

(Sem client próprio: `rclone authorize "drive"`.) O navegador abre, você autoriza, e o terminal imprime um JSON
`{"access_token":...,"refresh_token":...}`. Copie o JSON inteiro. **Trate-o como senha.**

## 3. Criar o remote na VPS

```bash
cd /opt/vps-backup-manager
sudo ./scripts/rclone-config.sh
```

Respostas no assistente:

| Pergunta | Resposta |
|---|---|
| `n` (New remote) → name | `gdrive` |
| Storage | `drive` |
| client_id / client_secret | os seus (ou vazio) |
| scope | **`drive.file`** (recomendado: o rclone só enxerga arquivos que ele próprio criou — menor privilégio). Use `drive` só se precisar ler arquivos criados por outros meios. |
| service_account_file | vazio |
| Edit advanced config | `n` |
| Use web browser to automatically authenticate? | **`n`** (VPS sem navegador) |
| config_token | cole o JSON do passo 2 |
| Configure as Shared Drive? | `n` (ou `y` se usar Drive compartilhado) |
| Keep this remote? | `y` → `q` |

Testar:

```bash
sudo ./scripts/rclone-config.sh test gdrive
```

## 4. Conectar ao aplicativo

Painel → **Configurações → Google Drive (rclone)**:

- Remote do rclone: `gdrive`
- Pasta de destino: `Backups`
- Subpasta da VPS: `VPS-Producao` (marque "Criar subpasta por VPS")

Clique **Salvar e testar upload**: o app cria a pasta, envia um arquivo de teste, confere e apaga.
O resultado aparece no dashboard ("Google Drive: Conectado").

Estrutura gerada:

```
Backups/VPS-Producao/2026/10/01/mysql/MySQL_principal/ecommerce_2026-10-01_03-00-00.sql.gz
Backups/VPS-Producao/2026/10/01/mysql/MySQL_principal/ecommerce_2026-10-01_03-00-00.sql.gz.sha256
Backups/VPS-Producao/2026/10/01/postgresql/PG/crm_2026-10-01_03-00-00.dump.gz
```

## Tokens: renovação e problemas

| Sintoma | Causa / solução |
|---|---|
| Erro "Token do Google Drive expirado ou revogado" (`invalid_grant`) | App OAuth em modo *Teste* (7 dias), senha da conta trocada ou acesso revogado. Publique o app e rode `sudo ./scripts/rclone-config.sh reconnect gdrive` (precisa do `rclone authorize` no PC novamente). |
| "Remote não existe no rclone.conf" | Nome do remote no painel diferente do configurado. |
| "Cota do Google Drive excedida" | Libere espaço / reduza retenção. |
| Rate limit (403 userRateLimitExceeded) | Use client ID próprio. O app já faz retries com backoff. |

## Segurança

- `rclone/rclone.conf` contém o refresh token: `chmod 600`, fora do Git (`.gitignore`), dono UID 1654.
- Com escopo `drive.file`, um vazamento do token não expõe o restante do seu Drive.
- Considere uma conta Google dedicada para backups com 2FA.
- Para criptografar os arquivos no Drive, ative **age** (Configurações → Criptografia). Ver `06-restore.md`.

## Gerar chave age (criptografia opcional)

No **seu PC** (não na VPS):

```bash
age-keygen -o age-key.txt          # guarda a chave PRIVADA — faça cópias seguras (cofre de senhas, pendrive)
grep 'public key' age-key.txt      # age1....  → cole em Configurações → Chaves públicas age
```

Sem a chave privada os backups criptografados **não podem ser restaurados**.
