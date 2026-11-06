# テスト用の OpenSSH サーバー（SFTP）
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends openssh-server \
 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash hakobune && echo 'hakobune:hakobune-pass' | chpasswd
RUN mkdir -p /run/sshd && ssh-keygen -A
COPY hakobune.conf /etc/ssh/sshd_config.d/hakobune.conf
CMD ["/usr/sbin/sshd", "-D", "-e"]
