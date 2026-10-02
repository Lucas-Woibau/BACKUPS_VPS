# Fase 9 — Checklist de teste inicial

Execute após instalar, configurar o Drive e cadastrar ao menos uma conexão.

## 1. Saúde

- [ ] `docker compose ps` → `vps-backup-manager` **healthy**
- [ ] `curl -s http://127.0.0.1:8080/api/health` → `{"status":"ok","checks":{...true}}`
- [ ] Dashboard mostra **Sistema: Online**

## 2. Conexões

- [ ] Conexões → **Testar** → "Conexão realizada com sucesso" + versão do servidor
- [ ] Botão **Bancos** lista os bancos esperados e marca os incluídos

## 3. Backup manual

- [ ] Dashboard → **Executar backup agora** → janela da execução atualiza sozinha
- [ ] Status final **Sucesso** em todos os bancos
- [ ] Clique duas vezes rápido em "Executar backup agora": a segunda tentativa retorna "Já existe um backup em execução" (lock)

## 4. Dump, compactação e checksum

No **Histórico**, abra um backup:

- [ ] "Tamanho original" > 0 e "Final" menor (compactado)
- [ ] SHA-256 preenchido
- [ ] Destino no formato `gdrive:Backups/VPS-Producao/AAAA/MM/DD/<tipo>/<conexão>/<banco>_AAAA-MM-DD_HH-mm-ss.sql.gz`
- [ ] Tentativas de upload = 1 (normal)

## 5. Arquivo no Google Drive

- [ ] `sudo ./scripts/rclone-config.sh test gdrive`
- [ ] `docker compose exec -T app rclone lsl gdrive:Backups/VPS-Producao --max-depth 6 | tail`
- [ ] No navegador, drive.google.com → pasta `Backups/VPS-Producao/...` contém `.sql.gz` e `.sha256`

## 6. Integridade de ponta a ponta

```bash
./scripts/fetch-backup.sh "COLE_O_DESTINO_DO_HISTÓRICO"
# → "....sql.gz: OK"
gunzip -t backups/restore/*.sql.gz && echo "gzip íntegro"
zcat backups/restore/*.sql.gz | tail -1        # MySQL: "-- Dump completed on ..."
```

- [ ] SHA-256 confere
- [ ] Faça um **restore de teste** em banco temporário (Fase 10) — só isso prova que o backup serve.

## 7. Limpeza local

- [ ] `ls backups/work` → vazio após a execução (sem "Manter cópia local")
- [ ] `df -h` estável

## 8. Histórico e logs

- [ ] Histórico lista a execução com início/fim/duração
- [ ] Logs → eventos da execução; `docker compose logs app | tail` mostra JSON sem senhas
- [ ] `grep -ri "SENHA-DO-BANCO" data/logs/ || echo "nenhuma senha nos logs"`

## 9. Scheduler

- [ ] Crie uma agenda para daqui a 5 minutos (ex.: hoje, horário atual + 5) → **Prévia** mostra o horário correto na sua timezone
- [ ] Após o horário: Histórico mostra execução com origem **Agendado**
- [ ] `docker compose restart app` → agendas continuam listadas com a mesma "Próxima execução"
- [ ] (Opcional) `sudo reboot` → após voltar, `docker compose ps` healthy e agenda preservada

## 10. Falhas simuladas

- [ ] Edite a conexão com senha errada → backup manual mostra erro "Acesso negado", outras conexões continuam
- [ ] Remote inválido nas Configurações → erro claro "Remote não existe no rclone.conf"
- [ ] Webhook (se usar): **Salvar e enviar teste** → recebido no destino

Volte as configurações corretas ao final.
