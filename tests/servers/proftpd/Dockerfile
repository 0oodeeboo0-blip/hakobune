# テスト用の ProFTPD（MLSD / MLST / MFMT に対応）
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends proftpd-core \
 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash hakobune && echo 'hakobune:hakobune-pass' | chpasswd
COPY proftpd.conf /etc/proftpd/hakobune.conf
CMD ["proftpd", "--nodaemon", "--config", "/etc/proftpd/hakobune.conf"]
