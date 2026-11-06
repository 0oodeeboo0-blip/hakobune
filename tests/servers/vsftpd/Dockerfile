# テスト用の vsftpd（FTP と、明示的 FTPS の両方を受け付ける）
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends vsftpd openssl \
 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash hakobune && echo 'hakobune:hakobune-pass' | chpasswd
RUN openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=localhost" \
      -keyout /etc/ssl/private/hakobune.key -out /etc/ssl/certs/hakobune.crt
RUN mkdir -p /var/run/vsftpd/empty
COPY vsftpd.conf /etc/vsftpd-hakobune.conf
CMD ["vsftpd", "/etc/vsftpd-hakobune.conf"]
