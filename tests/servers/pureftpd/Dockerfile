# テスト用の Pure-FTPd
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends pure-ftpd \
 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash hakobune && echo 'hakobune:hakobune-pass' | chpasswd
# -S: 待ち受け、-l unix: OS のユーザーでログイン、-E: 匿名を拒否、-j: ホームを作る、-H: 名前を引かない、
# -p / -P: パッシブモードのポートと IP、-c / -C: 同時接続数の上限
CMD ["pure-ftpd", "-S", "127.0.0.1,2122", "-l", "unix", "-E", "-j", "-H", "-p", "40100:40199", "-P", "127.0.0.1", "-c", "200", "-C", "200"]
