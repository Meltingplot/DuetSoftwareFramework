#!/bin/bash

# The files go to /opt/dsf/conf, where the dsf user may put a symlink in place of each of them. They belong to dsf in the end anyway,
# so they are made with the rights of dsf and root neither writes nor changes anything there
if [ "$(id -u)" -eq 0 ]; then
	exec setpriv --reuid=dsf --regid=dsf --clear-groups -- "$0" "$@"
fi

openssl req \
	-x509 \
	-newkey rsa:4096 \
	-sha256 \
	-days 1825 \
	-nodes \
	-keyout /opt/dsf/conf/https.key \
	-out /opt/dsf/conf/https.crt \
	-subj "/CN=$(hostname)" \
	-extensions v3_ca \
	-extensions v3_req \
	-config <( \
	echo '[req]'; \
	echo 'default_bits= 4096'; \
	echo 'distinguished_name=req'; \
	echo 'x509_extension = v3_ca'; \
	echo 'req_extensions = v3_req'; \
	echo '[v3_req]'; \
	echo 'basicConstraints = CA:FALSE'; \
	echo 'keyUsage = nonRepudiation, digitalSignature, keyEncipherment'; \
	echo 'subjectAltName = @alt_names'; \
	echo '[ alt_names ]'; \
	echo "DNS.1 = $(hostname)"; \
	echo '[ v3_ca ]'; \
	echo 'subjectKeyIdentifier=hash'; \
	echo 'authorityKeyIdentifier=keyid:always,issuer'; \
	echo 'basicConstraints = critical, CA:TRUE, pathlen:0'; \
	echo 'keyUsage = critical, cRLSign, keyCertSign'; \
	echo 'extendedKeyUsage = serverAuth, clientAuth')

openssl x509 -noout -text -in /opt/dsf/conf/https.crt
openssl pkcs12 -export -out /opt/dsf/conf/https.pfx --inkey /opt/dsf/conf/https.key -in /opt/dsf/conf/https.crt -passout pass:

chmod 660 /opt/dsf/conf/https.*