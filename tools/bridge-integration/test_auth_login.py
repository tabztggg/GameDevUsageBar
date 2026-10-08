"""Offline contract tests: never launch Claude, Firefox, WFP or auth-status."""
from __future__ import annotations
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock
import uuid

SCRIPTS = Path.home() / ".codex" / "skills" / "claude-code-bridge" / "scripts"
sys.path.insert(0, str(SCRIPTS))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import auth_login as login


class FakeJob:
    def __init__(self, bridge, command, environment, cwd, stdin, stdout, stderr, process_limit):
        self.bridge = bridge
        self.pid = 4242
        self.tree_exit_observed = False
        bridge.commands.append(command)
        bridge.environments.append(environment)
        bridge.limits.append(process_limit)
        bridge.events.append("created")
    def resume(self): self.bridge.events.append("resumed")
    def wait(self, seconds=None): return 0
    def terminate(self):
        self.bridge.events.append("terminated")
        self.tree_exit_observed = self.bridge.closed
        return self.bridge.closed
    def close(self): self.bridge.events.append("closed")


class AuthLoginTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="bridge-auth-fixture-", dir=Path(__file__).resolve().parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "claude-profiles"
        self.slot = str(uuid.uuid4())
        self.profile = self.root / uuid.UUID(self.slot).hex
        self.profile.mkdir(parents=True)
        (self.profile / login.OWNER_MARKER).write_text(json.dumps({"schema_version":1,"provider":"claude","slot_id":self.slot}))
        self.cli = Path(self.temp.name) / "claude-fixture.exe"
        self.cli.write_bytes(b"not executable")
        self.firefox = Path(self.temp.name) / "firefox-fixture.exe"
        self.firefox.write_bytes(b"not executable")
        self.bridge = types.SimpleNamespace(DEFAULT_POLICY=Path(self.temp.name)/"policy.json", PRODUCTION_STATE=Path(self.temp.name),
                                           CLI=self.cli,FIREFOX=self.firefox,BridgeError=RuntimeError,commands=[],environments=[],limits=[],events=[],closed=True)
        self.bridge.policy_load = lambda _: {"approved_cli_versions":["2.1.285"],"approved_cli_sha256":[login.PINNED_CLI_SHA256],
                                            "network_guard":{"proxy_ip":"192.0.2.10","proxy_port":8080},"browser":{}}
        self.bridge.file_hash = lambda p: login.PINNED_CLI_SHA256 if Path(p)==self.cli else hashlib.sha256(Path(p).read_bytes()).hexdigest()
        self.bridge.save = lambda p,v: Path(p).write_text(json.dumps(v,sort_keys=True),encoding="utf-8")
        self.bridge.WindowsJob = lambda *a,**k: FakeJob(self.bridge,*a,**k)
        @contextlib.contextmanager
        def serial(_):
            self.bridge.events.append("lock_enter")
            try: yield
            finally: self.bridge.events.append("lock_exit")
        patches = [mock.patch.object(login,"profile_root",return_value=self.root),
                   mock.patch.object(login.sys.stdin,"isatty",return_value=True),
                   mock.patch.object(login,"read_only_serial_lock",side_effect=serial),
                   mock.patch.object(login.browser,"auth_status",return_value={"status":"launch_checks_passed","executable":str(self.firefox)}),
                   mock.patch.object(login,"check_login_settings",return_value=[]),
                   mock.patch.object(login,"assert_network_guard",return_value={"verified":True}),
                   mock.patch.object(login,"_attended",return_value=(0,False,{"browser_launch_attempted":True,"browser_launch_requested":True,"browser_launch_unknown":False}))]
        self.patches = patches
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)
    def run_login(self, result=None):
        return login.dispatch(types.SimpleNamespace(config_dir=str(self.profile),result=result),self.bridge)
    def test_only_subscription_auth_command_and_containment(self):
        result=self.run_login()
        self.assertEqual(self.bridge.commands,[[str(self.cli),"--safe-mode","auth","login","--claudeai"]])
        self.assertEqual(self.bridge.limits,[1])
        self.assertEqual(result["status"],"completed_unverified")
        self.assertTrue(result["process_closed"])
        self.assertFalse(result["account_status_verified"])
        self.assertLess(self.bridge.events.index("closed"),self.bridge.events.index("lock_exit"))
    def test_environment_no_keys_browser_or_proxy_bypass_inherited(self):
        env=login.child_environment(self.profile,{"proxy_ip":"192.0.2.10","proxy_port":8080},self.firefox,
          {"ANTHROPIC_API_KEY":"fixture-secret","AWS_SECRET_ACCESS_KEY":"fixture-secret","GITHUB_TOKEN":"fixture-secret",
           "BROWSER":"bad-browser","NO_PROXY":"*","PYTHONPATH":"unsafe","NODE_OPTIONS":"unsafe","PATH":"unsafe","SystemRoot":r"C:\Windows"})
        self.assertNotIn("fixture-secret",str(env))
        self.assertNotIn("PATH",env)
        self.assertEqual(env["BROWSER"],str(self.firefox))
        self.assertEqual(env["NO_PROXY"],"")
        self.assertEqual(env["HTTPS_PROXY"],"http://192.0.2.10:8080")
        self.assertEqual(env["CLAUDE_CONFIG_DIR"],str(self.profile))
        self.assertEqual(login.browser_environment({"API_KEY":"fixture-secret","BROWSER":"bad","SystemRoot":"windows"}),{"SystemRoot":"windows"})
    def test_default_or_other_profile_is_rejected(self):
        with self.assertRaises(login.LoginBlocked): login.validate_profile(str(Path.home()/".claude"))
        with self.assertRaises(login.LoginBlocked): login.validate_profile(str(Path(self.temp.name)))
        self.assertEqual(self.bridge.commands,[])
    def test_missing_or_wrong_owner_rejected(self):
        marker=self.profile/login.OWNER_MARKER
        marker.unlink()
        with self.assertRaises(login.LoginBlocked): self.run_login()
        marker.write_text(json.dumps({"schema_version":1,"provider":"claude","slot_id":str(uuid.uuid4())}))
        with self.assertRaises(login.LoginBlocked): self.run_login()
        self.assertEqual(self.bridge.commands,[])
    def test_profile_credential_hardlink_rejected_before_auth(self):
        source=Path(self.temp.name)/"other-fixture-credential"
        source.write_text("opaque fixture")
        credential=self.profile/".credentials.json"
        credential.write_text("opaque fixture")
        original_stat=Path.stat
        def linked_metadata(path,*args,**kwargs):
            observed=original_stat(path,*args,**kwargs)
            if path==credential:
                values={name:getattr(observed,name) for name in dir(observed) if name.startswith("st_")}
                values["st_nlink"]=2  # No real alias is created.
                return types.SimpleNamespace(**values)
            return observed
        with mock.patch.object(Path,"stat",linked_metadata):
            with self.assertRaises(login.LoginBlocked): self.run_login()
        self.assertEqual(source.read_text(),"opaque fixture")
        self.assertEqual(self.bridge.commands,[])
    def test_network_path_rejected_without_touching_it(self):
        with self.assertRaises(login.LoginBlocked): login.no_aliases(Path(r"\\fixture-server\fixture-share\profile"))
    def test_existing_receipt_never_overwritten(self):
        result=self.profile/"receipt.json"
        result.write_text("original fixture receipt")
        with self.assertRaises(login.LoginBlocked): self.run_login(str(result))
        self.assertEqual(result.read_text(),"original fixture receipt")
        self.assertEqual(self.bridge.commands,[])
    def test_unknown_previous_effect_is_preserved(self):
        state=self.profile/login.LOGIN_STATE
        previous={"schema_version":1,"slot_id":self.slot,"config_dir_sha256":login.path_hash(self.profile),"status":"unknown","process_closed":True}
        state.write_text(json.dumps(previous))
        result=self.run_login()
        self.assertEqual(result["status"],"blocked")
        self.assertEqual(json.loads(state.read_text()),previous)
        self.assertEqual(self.bridge.commands,[])
    def test_completed_owned_profile_allows_new_attended_relogin(self):
        self.run_login()
        credential=self.profile/".credentials.json"
        credential.write_text("opaque fixture credential")
        result=self.run_login()
        self.assertEqual(result["status"],"completed_unverified")
        self.assertFalse(result["credential_changed"])
        self.assertTrue(result["credential_change_verified"])
        self.assertEqual(credential.read_text(),"opaque fixture credential")
    def test_changed_credential_does_not_prove_account(self):
        def attended(*_):
            (self.profile/".credentials.json").write_text("new opaque fixture")
            return 0,False,{"browser_launch_unknown":False}
        with mock.patch.object(login,"_attended",side_effect=attended): result=self.run_login()
        self.assertTrue(result["credential_changed"])
        self.assertFalse(result["account_status_verified"])
        self.assertNotIn("new opaque fixture",json.dumps(result))
    def test_changed_cli_blocks_before_native_command(self):
        self.bridge.file_hash=lambda _:"unapproved"
        result=self.run_login()
        self.assertEqual(result["status"],"blocked")
        self.assertEqual(self.bridge.commands,[])
    def test_unapproved_browser_blocks_before_native_command(self):
        with mock.patch.object(login.browser,"auth_status",return_value={"status":"launch_checks_passed","executable":str(self.cli)}): result=self.run_login()
        self.assertEqual(result["status"],"blocked")
        self.assertEqual(self.bridge.commands,[])
    def test_network_guard_blocks_before_native_command(self):
        with mock.patch.object(login,"assert_network_guard",return_value={"verified":False}): result=self.run_login()
        self.assertEqual(result["status"],"blocked")
        self.assertEqual(self.bridge.commands,[])
    def test_unattended_launch_refused(self):
        with mock.patch.object(login.sys.stdin,"isatty",return_value=False):
            with self.assertRaises(login.LoginBlocked): self.run_login()
        self.assertEqual(self.bridge.commands,[])
    def test_cancellation_has_closure_but_unknown_auth_effect(self):
        with mock.patch.object(login,"_attended",return_value=(None,True,{"browser_launch_unknown":False})): result=self.run_login()
        self.assertEqual(result["status"],"unknown")
        self.assertTrue(result["process_closed"])
        self.assertEqual(len(self.bridge.commands),1)
    def test_process_closure_failure_is_unknown(self):
        self.bridge.closed=False
        result=self.run_login()
        self.assertEqual(result["status"],"unknown")
        self.assertFalse(result["process_closed"])
    def test_native_creation_exception_is_unknown_no_retry(self):
        self.bridge.WindowsJob=mock.Mock(side_effect=RuntimeError("fixture failure"))
        result=self.run_login()
        self.assertEqual(result["status"],"unknown")
        self.assertFalse(result["process_closed"])
        self.bridge.WindowsJob.assert_called_once()
    def test_terminal_exception_closes_before_lock_release(self):
        with mock.patch.object(login,"_attended",side_effect=KeyboardInterrupt): result=self.run_login()
        self.assertEqual(result["status"],"unknown")
        self.assertTrue(result["process_closed"])
        self.assertLess(self.bridge.events.index("closed"),self.bridge.events.index("lock_exit"))
    def test_nonzero_natural_exit_preserves_failed_status(self):
        with mock.patch.object(login,"_attended",return_value=(1,False,{"browser_launch_unknown":False})): result=self.run_login()
        self.assertEqual(result["status"],"failed")
        self.assertTrue(result["process_closed"])
    def test_receipt_binds_profile_no_auth_material(self):
        result_path=Path(self.temp.name)/"unique-result.json"
        result=self.run_login(str(result_path))
        self.assertEqual(json.loads(result_path.read_text()),result)
        canonical=str(self.profile.resolve()).replace("/","\\").rstrip("\\").casefold()
        self.assertEqual(result["config_dir_sha256"],hashlib.sha256(canonical.encode()).hexdigest())
        self.assertEqual(str(uuid.UUID(result["attempt_id"])),result["attempt_id"])
        self.assertNotIn("https://",json.dumps(result))
    def test_auth_url_scope_and_partial_stream(self):
        good="https://claude.ai/oauth/authorize?state=fixture-state&code_challenge=fixture-challenge"
        self.assertEqual(login.captured_auth_url("If the browser didn't open, visit: "+good+"\n"),good)
        self.assertIsNone(login.captured_auth_url("If the browser didn't open, visit: "+good))
        self.assertIsNone(login.captured_auth_url("Advertisement "+good+"\n"))
        authentic="https://claude.com/cai/oauth/authorize?code=true&client_id=fixture-client&response_type=code&state=fixture-state&code_challenge=fixture-challenge"
        self.assertEqual(login.captured_auth_url("If the browser didn't open, visit: "+authentic+"\n"),authentic)
        for url in ("http://claude.ai/oauth/authorize?x=1","https://evil.invalid/oauth/authorize?x=1","https://claude.ai/settings/usage","https://claude.ai/oauth/authorize?x=1#code","https://claude.ai:444/oauth/authorize?x=1"):
            with self.assertRaises((login.LoginBlocked,login.browser.BrowserError)): login.validate_auth_url(url)
    def test_browser_exception_is_not_retried_and_no_url_logged(self):
        # Exercise actual transient-stream pump with synchronous mock threads.
        self.patches[-1].stop()
        class ImmediateThread:
            def __init__(self,target,args=(),daemon=False): self.target,self.args=target,args
            def start(self): self.target(*self.args)
        native=types.SimpleNamespace(wait=lambda _:0)
        url="https://claude.com/cai/oauth/authorize?code=true&state=fixture-secret-state&code_challenge=fixture-secret-challenge"
        out=io.BytesIO(("If the browser didn't open, visit: "+url+"\n"+"If the browser didn't open, visit: "+url+"\n").encode())
        shown=io.StringIO()
        with mock.patch.object(login.threading,"Thread",ImmediateThread),mock.patch.object(login.getpass,"getpass",return_value=":cancel"),\
             mock.patch.object(login.browser,"open_auth_browser",side_effect=OSError("fixture uncertain")) as opener,mock.patch.object(login.sys,"stdout",shown):
            code,cancelled,info=login._attended(native,out,io.BytesIO(),io.BytesIO(),{},self.profile,str(uuid.uuid4()))
        opener.assert_called_once()
        self.assertTrue(info["browser_launch_unknown"])
        self.assertNotIn(url,shown.getvalue())
        self.assertNotIn("fixture-secret",shown.getvalue())


if __name__ == "__main__": unittest.main()
