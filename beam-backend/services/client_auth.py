"""Installation capability authentication; no shared account or server-funded quota.

The desktop generates a cryptographically random 256-bit bearer credential. Its
SHA-256 digest identifies a private vault namespace. Possession grants access only
to that namespace; inventing a credential never grants access to existing keys.
"""
import hashlib
import re
from fastapi import Header, HTTPException

AUTH_SCHEME = "installation-bearer-v1"


def authenticated_client(authorization: str | None = Header(default=None)) -> str:
    scheme, _, token = (authorization or "").partition(" ")
    if scheme.lower() != "bearer" or not re.fullmatch(r"[0-9a-f]{64}", token):
        raise HTTPException(401, "A valid installation credential is required",
                            headers={"WWW-Authenticate": "Bearer"})
    return hashlib.sha256(token.encode("ascii")).hexdigest()
