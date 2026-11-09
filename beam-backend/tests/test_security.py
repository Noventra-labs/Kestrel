import asyncio
import hashlib
import importlib
import json
import os
import unittest
from unittest.mock import patch

import httpx
from fastapi.testclient import TestClient

SECRET = "test-only-secret-" + "x" * 40
with patch.dict(os.environ, {"VAULT_ENCRYPTION_SECRET": SECRET}, clear=True), patch("dotenv.load_dotenv"):
    main = importlib.import_module("main")
from services.llm_router import LLMRouter
from services.supabase_vault import SupabaseKeyVault


class SecurityTests(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, {"VAULT_ENCRYPTION_SECRET": SECRET}, clear=True)
        self.env.start()
        self.router = LLMRouter()
        self.router.vault.supabase_url = "https://vault.invalid"
        self.router.vault.supabase_key = "test-service-role"
        self.rows = {}
        self.provider_calls = 0
        self.writes = 0
        self.fail_storage = False
        self.last_messages = None
        self.router._client = httpx.AsyncClient(transport=httpx.MockTransport(self.transport))
        self.router_patch = patch.object(main, "router", self.router)
        self.router_patch.start()
        self.client = TestClient(main.app).__enter__()

    def tearDown(self):
        self.client.__exit__(None, None, None)
        self.router_patch.stop()
        self.env.stop()

    @staticmethod
    def auth(character):
        return {"Authorization": "Bearer " + character * 64}

    def transport(self, request):
        if request.url.host == "vault.invalid":
            if self.fail_storage:
                return httpx.Response(503, text="storage offline")
            if request.method == "POST":
                row = json.loads(request.content)
                self.rows[(row["client_id"], row["provider"])] = row
                self.writes += 1
                return httpx.Response(201, json=[row])
            client_id = request.url.params["client_id"].removeprefix("eq.")
            provider = request.url.params.get("provider", "").removeprefix("eq.")
            if request.method == "DELETE":
                self.rows.pop((client_id, provider), None)
                return httpx.Response(204)
            return httpx.Response(200, json=[v for (c, p), v in self.rows.items()
                                             if c == client_id and (not provider or p == provider)])
        self.provider_calls += 1
        self.last_messages = json.loads(request.content)["contents"]
        answer = "answer-" + str(self.provider_calls)
        payload = {"candidates": [{"content": {"parts": [{"text": answer}]}}]}
        return httpx.Response(200, text="data: " + json.dumps(payload) + "\n\n")

    def chat(self, client="a", key="test-provider-key", messages=None):
        body = {"model": "gemini-test", "messages": messages or [{"role": "user", "content": "hello"}]}
        if key is not None:
            body["api_key_override"] = key
        return self.client.post("/v1/chat", headers=self.auth(client), json=body)

    def test_all_private_routes_reject_missing_auth(self):
        for method, path, body in [
            ("POST", "/v1/keys", {"provider": "gemini", "api_key": "test"}),
            ("GET", "/v1/keys", None), ("DELETE", "/v1/keys/gemini", None),
            ("POST", "/v1/chat", {"messages": [{"role": "user", "content": "hi"}]}),
            ("POST", "/v1/cache/clear", None),
        ]:
            with self.subTest(path=path, method=method):
                self.assertEqual(401, self.client.request(method, path, json=body).status_code)
        self.assertEqual({}, self.rows)

    def test_invalid_credential_rejected(self):
        self.assertEqual(401, self.client.get("/v1/keys", headers={"Authorization": "Bearer default"}).status_code)

    def test_caller_cannot_assign_another_owner(self):
        response = self.client.post("/v1/keys", headers=self.auth("a"),
                                    json={"provider": "gemini", "api_key": "test", "client_id": "victim"})
        self.assertEqual(422, response.status_code)

    def test_query_parameter_cannot_access_or_delete_other_installation(self):
        saved = self.client.post("/v1/keys", headers=self.auth("a"), json={"provider": "gemini", "api_key": "key-a"})
        owner = saved.json()["client_id"]
        self.assertEqual(hashlib.sha256(("a" * 64).encode()).hexdigest(), owner)
        self.assertEqual([], self.client.get("/v1/keys?client_id=" + owner, headers=self.auth("b")).json()["keys"])
        self.client.delete("/v1/keys/gemini?client_id=" + owner, headers=self.auth("b"))
        self.assertIn((owner, "gemini"), self.rows)
        self.client.delete("/v1/keys/gemini", headers=self.auth("a"))
        self.assertNotIn((owner, "gemini"), self.rows)

    def test_cache_isolation_and_override_processing(self):
        first = self.chat().text
        self.assertEqual(first, self.chat().text)
        self.assertEqual(1, self.provider_calls)
        self.assertEqual(2, self.writes)  # override still processed on a cache hit
        self.assertNotEqual(first, self.chat("b").text)
        self.assertEqual(2, self.provider_calls)
        self.chat(key="rotated-key")
        self.assertEqual(3, self.provider_calls)

    def test_cache_clear_is_scoped(self):
        self.chat("a")
        self.chat("b")
        self.assertEqual(1, self.client.post("/v1/cache/clear", headers=self.auth("a")).json()["entries_removed"])
        self.chat("b")
        self.assertEqual(2, self.provider_calls)
        self.chat("a")
        self.assertEqual(3, self.provider_calls)

    def test_no_server_key_fallback(self):
        os.environ["GEMINI_API_KEY"] = "server-key-must-never-be-used"
        response = self.chat(key=None)
        self.assertIn("Configure your gemini API key", response.text)
        self.assertEqual(0, self.provider_calls)

    def test_failed_persistence_is_not_success(self):
        self.fail_storage = True
        response = self.client.post("/v1/keys", headers=self.auth("a"), json={"provider": "gemini", "api_key": "test"})
        self.assertEqual(503, response.status_code)
        self.assertEqual({}, self.rows)

    def test_unconfigured_vault_is_not_success(self):
        self.router.vault.supabase_url = ""
        response = self.client.post("/v1/keys", headers=self.auth("a"), json={"provider": "gemini", "api_key": "test"})
        self.assertEqual(503, response.status_code)

    def test_conversation_roles_reach_provider(self):
        self.chat(messages=[{"role": "user", "content": "first"},
                            {"role": "assistant", "content": "answer"},
                            {"role": "user", "content": "explain that"}])
        self.assertEqual(["user", "model", "user"], [m["role"] for m in self.last_messages])
        self.assertEqual("answer", self.last_messages[1]["parts"][0]["text"])

    def test_missing_or_public_encryption_secret_rejected(self):
        for secret in ("", "short", "noventra-labs-beam-default-vault-secret-2026",
                       "change-this-to-a-strong-32-char-secret-key"):
            with self.subTest(secret=secret), patch.dict(os.environ, {"VAULT_ENCRYPTION_SECRET": secret}):
                with self.assertRaises(RuntimeError):
                    SupabaseKeyVault()

    def test_encryption_secrets_are_isolated(self):
        encrypted = self.router.vault.encrypt_key("private-provider-key")
        self.assertEqual("private-provider-key", self.router.vault.decrypt_key(encrypted))
        with patch.dict(os.environ, {"VAULT_ENCRYPTION_SECRET": "different-secret-" + "z" * 40}):
            self.assertIsNone(SupabaseKeyVault().decrypt_key(encrypted))


if __name__ == "__main__":
    unittest.main()
