NAME=server
EXPIRE=36500
CN=localhost

openssl genpkey -algorithm ED25519 -out ${NAME}.key.pem
openssl req -new -key ${NAME}.key.pem -out ${NAME}.csr.pem -subj "/C=UK/ST=London/L=London/O=TinyCam/OU=Dev/CN=${CN}"
openssl x509 -req -days ${EXPIRE} -in ${NAME}.csr.pem -signkey ${NAME}.key.pem -out ${NAME}.cert.pem