#!/usr/bin/env bash
# CI でテスト用の FTP サーバーを立てる。Docker のコンテナを、ホストのネットワークで動かす。
#   vsftpd   127.0.0.1:2121（FTP と明示的 FTPS）
#   Pure-FTPd 127.0.0.1:2122
#   ProFTPD  127.0.0.1:2123
#   OpenSSH  127.0.0.1:2222（SFTP）
set -euo pipefail
cd "$(dirname "$0")"

for name in vsftpd pureftpd proftpd sshd; do
  echo "::group::build $name"
  docker build -t "hakobune-test-$name" "$name"
  echo "::endgroup::"
  extra=()
  # Debian の pure-ftpd は Linux のケーパビリティを切り替えるので、通常のコンテナの権限では起動できない
  [ "$name" = pureftpd ] && extra=(--privileged)
  # --init: サーバーを PID 1 にしない（子プロセスの後始末やシグナルの扱いを init に任せる）
  docker run -d --init --network host "${extra[@]}" --name "hakobune-$name" "hakobune-test-$name" >/dev/null
done

wait_for_port() {
  local name=$1 port=$2
  for _ in $(seq 1 30); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
      echo "$name is listening on $port"
      return 0
    fi
    sleep 1
  done
  echo "::error::$name did not start on port $port"
  docker ps -a --filter "name=hakobune-$name" --format '{{.Names}}: {{.Status}}' || true
  docker logs "hakobune-$name" || true
  return 1
}

status=0
wait_for_port vsftpd 2121 || status=1
wait_for_port pureftpd 2122 || status=1
wait_for_port proftpd 2123 || status=1
wait_for_port sshd 2222 || status=1

# 待ち受けを確かめたあとも動き続けているか
sleep 3
docker ps -a --filter "name=hakobune-" --format '{{.Names}}: {{.Status}}'
exit $status
