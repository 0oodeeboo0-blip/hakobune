#!/usr/bin/env bash
# CI でテスト用の FTP サーバーを立てる。Docker のコンテナを、ホストのネットワークで動かす。
#   vsftpd   127.0.0.1:2121（FTP と明示的 FTPS）
#   Pure-FTPd 127.0.0.1:2122
#   ProFTPD  127.0.0.1:2123
set -euo pipefail
cd "$(dirname "$0")"

for name in vsftpd pureftpd proftpd; do
  echo "::group::build $name"
  docker build -t "hakobune-test-$name" "$name"
  echo "::endgroup::"
  docker run -d --network host --name "hakobune-$name" "hakobune-test-$name" >/dev/null
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
  docker logs "hakobune-$name" || true
  return 1
}

wait_for_port vsftpd 2121
wait_for_port pureftpd 2122
wait_for_port proftpd 2123
