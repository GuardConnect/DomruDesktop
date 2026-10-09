"""Generate disposable, independent Web Push fixtures locally; never publish keys."""
import base64
import json
import os
from pathlib import Path
import http_ece
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization

receiver = ec.generate_private_key(ec.SECP256R1())
sender = ec.generate_private_key(ec.SECP256R1())
secret, salt = os.urandom(16), os.urandom(16)
def public(key):
    return key.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
def b64(data):
    return base64.urlsafe_b64encode(data).decode().rstrip("=")
plain = b'{"data":{"PushType":"CALL_INCOMING","Call-ID":"test-call"}}'
vectors = []
for encoding in ("aesgcm", "aes128gcm"):
    cipher = http_ece.encrypt(plain, salt=salt, private_key=sender, dh=public(receiver), auth_secret=secret, version=encoding)
    vectors.append(dict(encoding=encoding, cipher=b64(cipher), privateKey=b64(receiver.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())), secret=b64(secret), cryptoHeader="p256ecdsa=ignored;dh="+b64(public(sender)), saltHeader="salt="+b64(salt)+";rs=4096", plain=plain.decode()))
Path(__file__).with_name("crypto-vectors.json").write_text(json.dumps(vectors), encoding="utf-8")
print("Generated two local Web Push fixtures (keys are not printed).")
