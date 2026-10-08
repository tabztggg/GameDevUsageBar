"""Synthetic files and mocked launches only: no Firefox/Claude/network/auth."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest import mock
import uuid

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path.home()/".codex"/"skills"/"claude-code-bridge"/"scripts"))
sys.path.insert(0, str(HERE))
import browser
import auth_login


class FreshBrowserTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="fresh-browser-fixture-", dir=HERE)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.skill = self.root / "skill"
        self.exe = self.skill/"state"/"firefox"/"firefox.exe"
        self.exe.parent.mkdir(parents=True)
        self.exe.write_bytes(b"NONEXECUTABLE fixture Firefox bytes")
        policies = self.exe.parent/"distribution"/"policies.json"
        policies.parent.mkdir()
        proxy = {"Mode":"manual","Locked":True,"HTTPProxy":"192.0.2.10:8080","SSLProxy":"192.0.2.10:8080","UseHTTPProxyForAllProtocols":True,"Passthrough":""}
        policies.write_text(json.dumps({"policies":{"Proxy":proxy,"DNSOverHTTPS":{"Enabled":False,"Locked":True},
            "Preferences":{name:{"Value":False,"Status":"locked"} for name in ("media.peerconnection.enabled","network.http.http3.enable")},"DisableAppUpdate":True}}))
        self.shared = self.skill/"state"/"firefox-profile"
        self.shared.mkdir()
        # Existing account A state must never be read, copied or changed.
        for name in ("cookies.sqlite","places.sqlite","logins.json","key4.db","prefs.js","sessionstore.jsonlz4"):
            (self.shared/name).write_bytes(b"UNRELATED ACCOUNT A FIXTURE STATE")
        (self.shared/"extensions").mkdir()
        (self.shared/"extensions"/"fixture.xpi").write_bytes(b"UNRELATED EXTENSION")
        self.shared_before = {str(p.relative_to(self.shared)):p.read_bytes() for p in self.shared.rglob("*") if p.is_file()}
        app = {"app_path":str(self.exe),"app_sha256":browser.digest(self.exe),"guard_id":"claude-bridge-fixture","sublayer_weight":65505,"proxy_ip":"192.0.2.10","proxy_port":8080}
        self.config = {"executable":str(self.exe),"executable_sha256":browser.digest(self.exe),"profile_dir":str(self.shared),
            "policies_path":str(policies),"policies_sha256":browser.digest(policies),"applications":[app],
            "profile_user_js_sha256":hashlib.sha256(browser.canonical_user_js(app)).hexdigest()}
        self.accounts = self.root/"GameDevBar"/"claude-profiles"
        self.slot = uuid.uuid4()
        self.account = self.new_account(self.slot)
        self.attempt = str(uuid.uuid4())
        self.url = "https://claude.com/cai/oauth/authorize?code=true&state=SYNTHETIC_SECRET_STATE&code_challenge=SYNTHETIC_CHALLENGE"
        self.patches = [mock.patch.object(browser,"ROOT",self.skill),
                        mock.patch.object(browser,"auth_account_root",return_value=self.accounts),
                        mock.patch.object(browser,"assert_network_guard",return_value={"verified":True}),
                        mock.patch.object(browser.subprocess,"Popen",return_value=types.SimpleNamespace(pid=7654))]
        self.mocks = [p.start() for p in self.patches]
        for p in self.patches: self.addCleanup(p.stop)
        self.spawn = self.mocks[-1]
    def new_account(self, slot):
        path = self.accounts/slot.hex
        path.mkdir(parents=True)
        (path/browser.AUTH_OWNER).write_text(json.dumps({"schema_version":1,"provider":"claude","slot_id":str(slot)}))
        return path
    def open(self, **kwargs):
        return browser.open_auth_browser(self.config,self.url,self.account,self.attempt,**kwargs)
    def prepared(self):
        return browser.prepare_auth_profile(self.config,self.account,self.attempt)
    def assert_shared_untouched(self):
        self.assertEqual(self.shared_before,{str(p.relative_to(self.shared)):p.read_bytes() for p in self.shared.rglob("*") if p.is_file()})
    def test_launch_fresh_profile_independent_no_remote_and_no_shared_state_read(self):
        original = Path.read_bytes
        def read(path):
            if path.is_relative_to(self.shared): self.fail("Shared Firefox state was read")
            return original(path)
        with mock.patch.object(Path,"read_bytes",read): result=self.open()
        profile=Path(result["browser_profile_dir"])
        self.assertEqual(profile,self.account/browser.AUTH_CONTAINER/uuid.UUID(self.attempt).hex/"profile")
        self.assertEqual({p.name for p in profile.iterdir()},{"user.js"})
        self.assertEqual(self.spawn.call_args.args[0],[str(self.exe),"--no-remote","--profile",str(profile),"--new-window",self.url])
        self.assertFalse(self.spawn.call_args.kwargs["shell"])
        self.assertEqual(self.spawn.call_args.kwargs["stdin"],browser.subprocess.DEVNULL)
        self.assertFalse(result["browser_tree_closed_verified"])
        self.assertTrue(result["browser_profile_fresh"])
        self.assert_shared_untouched()
    def test_two_attempts_and_slots_never_share_browser_session(self):
        a=self.open()
        first=Path(a["browser_profile_dir"])
        (first/"cookies.sqlite").write_text("NEW AUTH A SYNTHETIC STATE")
        self.attempt=str(uuid.uuid4())
        b=self.open()
        self.account=self.new_account(uuid.uuid4())
        self.attempt=str(uuid.uuid4())
        c=self.open()
        paths={a["browser_profile_dir"],b["browser_profile_dir"],c["browser_profile_dir"]}
        self.assertEqual(len(paths),3)
        self.assertTrue(all({p.name for p in Path(r["browser_profile_dir"]).iterdir()}=={"user.js"} for r in (b,c)))
        self.assertEqual((first/"cookies.sqlite").read_text(),"NEW AUTH A SYNTHETIC STATE")
        self.assert_shared_untouched()
    def test_same_attempt_refused_without_overwrite_or_launch_replay(self):
        result=self.open()
        marker=Path(result["browser_profile_dir"]).parent/browser.AUTH_MARKER
        before=marker.read_bytes()
        with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_called_once()
        self.assertEqual(marker.read_bytes(),before)
    def test_env_drops_all_profile_overrides_keys_and_uses_fresh_roots(self):
        contaminated={name:"INHERITED_UNSAFE" for name in ("MOZ_PROFILE","MOZ_RESET_PROFILE_RESTART","MOZ_LEGACY_PROFILES","XRE_PROFILE_PATH","XRE_PROFILE_LOCAL_PATH","SELECTABLE_PROFILE_RESET_PATH","SELECTABLE_PROFILE_RESET_STORE_ID","SELECTABLE_PROFILE_STORE_ID","ANTHROPIC_API_KEY","CLAUDE_CODE_OAUTH_TOKEN","BROWSER","PYTHONPATH","PATH","APPDATA","LOCALAPPDATA","HOME","USERPROFILE","TMP","TEMP","NO_PROXY")}
        contaminated["SystemRoot"]=r"C:\Windows"
        result=self.open(environment=contaminated)
        env=self.spawn.call_args.kwargs["env"]
        container=Path(result["browser_profile_dir"]).parent
        self.assertNotIn("INHERITED_UNSAFE",str(env))
        for name in contaminated:
            if name not in {"SystemRoot","APPDATA","LOCALAPPDATA","HOME","USERPROFILE","TMP","TEMP","NO_PROXY"}: self.assertNotIn(name,env)
        expected={"APPDATA":"appdata","LOCALAPPDATA":"localappdata","HOME":"home","USERPROFILE":"home","TMP":"tmp","TEMP":"tmp"}
        for name,directory in expected.items():
            self.assertEqual(env[name],str(container/directory))
            self.assertEqual(list((container/directory).iterdir()),[])
        self.assertEqual(env["NO_PROXY"],"")
        self.assertEqual(env["HTTPS_PROXY"],"http://192.0.2.10:8080")
    def test_prefs_retain_exact_pinned_network_defaults_and_add_private_history_cache_guards(self):
        result=self.open()
        actual=(Path(result["browser_profile_dir"])/"user.js").read_bytes()
        base=browser.canonical_user_js(self.config["applications"][0])
        self.assertTrue(actual.startswith(base))
        self.assertEqual(hashlib.sha256(base).hexdigest(),self.config["profile_user_js_sha256"])
        for pref,value in {"identity.fxaccounts.enabled":False,"signon.rememberSignons":False,"security.osclientcerts.autoload":False,
            "network.http.windows-sso.enabled":False,"network.http.microsoft-entra-sso.enabled":False,
            "browser.privatebrowsing.autostart":True,"places.history.enabled":False,"browser.cache.disk.enable":False}.items():
            self.assertIn(('user_pref('+json.dumps(pref)+', '+json.dumps(value)+');').encode(),actual)
    def test_contaminated_profile_or_environment_blocks_before_spawn(self):
        for relative in ("profile/cookies.sqlite","profile/prefs.js","profile/extensions","home/profiles.ini","appdata/Mozilla","tmp/preexisting"):
            with self.subTest(relative=relative):
                self.attempt=str(uuid.uuid4())
                container=self.prepared()
                entry=container/relative
                entry.parent.mkdir(parents=True,exist_ok=True)
                entry.write_text("unexpected synthetic state")
                with mock.patch.object(browser,"prepare_auth_profile",return_value=container):
                    with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_postprepare_contamination_during_last_guard_check_is_blocked(self):
        count=0
        original=browser.auth_status
        def status(config):
            nonlocal count
            result=original(config)
            count+=1
            if count==2:
                (self.account/browser.AUTH_CONTAINER/uuid.UUID(self.attempt).hex/"profile"/"cookies.sqlite").write_text("late fixture state")
            return result
        with mock.patch.object(browser,"auth_status",side_effect=status):
            with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_preferences_or_owner_marker_change_blocks(self):
        container=self.prepared()
        (container/"profile"/"user.js").write_text("changed")
        with mock.patch.object(browser,"prepare_auth_profile",return_value=container):
            with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
        self.attempt=str(uuid.uuid4())
        container=self.prepared()
        record=json.loads((container/browser.AUTH_MARKER).read_text())
        record["attempt_id"]=str(uuid.uuid4())
        (container/browser.AUTH_MARKER).write_text(json.dumps(record))
        with mock.patch.object(browser,"prepare_auth_profile",return_value=container):
            with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_network_guard_failure_or_pin_change_blocks_without_profile_launch(self):
        with mock.patch.object(browser,"assert_network_guard",return_value={"verified":False}):
            with self.assertRaises(browser.BrowserError): self.open()
        self.config["executable_sha256"]="unapproved"
        with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
        self.assertFalse((self.account/browser.AUTH_CONTAINER).exists())
    def test_wrong_slot_owner_default_network_and_attempt_identity_block(self):
        for profile,attempt in ((self.shared,self.attempt),(Path(r"\\fixture\share\profile"),self.attempt),(self.account,"non-GUID"),(self.account,uuid.UUID(self.attempt).hex)):
            with self.subTest(profile=profile,attempt=attempt):
                with self.assertRaises(browser.BrowserError): browser.open_auth_browser(self.config,self.url,profile,attempt)
        (self.account/browser.AUTH_OWNER).write_text(json.dumps({"schema_version":1,"provider":"claude","slot_id":str(uuid.uuid4())}))
        with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_reparse_metadata_and_shared_hardlink_metadata_are_rejected(self):
        container=self.prepared()
        target=container/"profile"/"user.js"
        original=Path.stat
        def metadata(path,*args,**kwargs):
            observed=original(path,*args,**kwargs)
            if path==target:
                values={name:getattr(observed,name) for name in dir(observed) if name.startswith("st_")}
                values["st_nlink"]=2
                return types.SimpleNamespace(**values)
            return observed
        with mock.patch.object(Path,"stat",metadata),mock.patch.object(browser,"prepare_auth_profile",return_value=container):
            with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
        def reparse(path,*args,**kwargs):
            observed=original(path,*args,**kwargs)
            if path==self.accounts:
                values={name:getattr(observed,name) for name in dir(observed) if name.startswith("st_")}
                values["st_file_attributes"]=0x400
                return types.SimpleNamespace(**values)
            return observed
        with mock.patch.object(Path,"stat",reparse):
            with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_generic_shared_browser_status_and_launch_still_use_original_profile(self):
        proxy=self.config["applications"][0]
        data=browser.canonical_user_js(proxy)
        (self.shared/"user.js").write_bytes(data)
        (self.shared/browser.PROFILE_MARKER).write_text(json.dumps({"schema_version":1,"kind":"claude-code-bridge-firefox-profile",
            "proxy_ip":proxy["proxy_ip"],"proxy_port":proxy["proxy_port"],"user_js_sha256":hashlib.sha256(data).hexdigest()}))
        checks=browser.status(self.config)
        self.assertEqual(checks["profile_dir"],str(self.shared))
        result=browser.open_browser(self.config,"https://claude.ai/settings/usage",environment={"SYSTEMROOT":r"C:\Windows"})
        self.assertEqual(result["profile_dir"],str(self.shared))
        self.assertEqual(self.spawn.call_args.args[0][3],str(self.shared))
        for name,content in self.shared_before.items(): self.assertEqual((self.shared/name).read_bytes(),content)
    def test_unknown_launch_retains_profile_and_marker_and_never_retries(self):
        self.spawn.side_effect=OSError("SYNTHETIC RAW ERROR MUST NOT APPEAR")
        with self.assertRaises(browser.AuthBrowserUnknown) as caught: self.open()
        metadata=caught.exception.metadata
        self.assertNotIn("SYNTHETIC",str(caught.exception))
        profile=Path(metadata["browser_profile_dir"])
        self.assertTrue(profile.is_dir())
        record=json.loads((profile.parent/browser.AUTH_MARKER).read_text())
        self.assertTrue(record["launch_attempted"])
        self.assertFalse(record["launch_requested"])
        self.assertNotIn(self.url,json.dumps(record))
        self.assertNotIn("SYNTHETIC_SECRET_STATE",json.dumps(record))
        with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_called_once()
        self.assertTrue(profile.exists())
    def test_record_write_exception_after_spawn_retains_pid_unknown_and_no_replay(self):
        original=Path.open
        writes=0
        def opened(path,*args,**kwargs):
            nonlocal writes
            mode=args[0] if args else kwargs.get("mode","r")
            if path.name==browser.AUTH_MARKER and mode=="w":
                writes+=1
                if writes==2: raise OSError("fixture receipt failure")
            return original(path,*args,**kwargs)
        with mock.patch.object(Path,"open",opened):
            with self.assertRaises(browser.AuthBrowserUnknown) as caught: self.open()
        self.assertEqual(caught.exception.metadata["browser_pid"],7654)
        self.spawn.assert_called_once()
        self.assertTrue(Path(caught.exception.metadata["browser_profile_dir"]).exists())
    def test_intent_write_failure_before_spawn_retains_attempt_and_prevents_reuse(self):
        original=Path.open
        def opened(path,*args,**kwargs):
            mode=args[0] if args else kwargs.get("mode","r")
            if path.name==browser.AUTH_MARKER and mode=="w": raise OSError("synthetic intent write failure")
            return original(path,*args,**kwargs)
        with mock.patch.object(Path,"open",opened):
            with self.assertRaises(OSError): self.open()
        self.spawn.assert_not_called()
        container=self.account/browser.AUTH_CONTAINER/uuid.UUID(self.attempt).hex
        self.assertTrue(container.is_dir())
        with self.assertRaises(browser.BrowserError): self.open()
        self.spawn.assert_not_called()
    def test_only_authorization_urls_accepted_no_url_in_record(self):
        for url in ("https://claude.ai/settings/usage","https://evil.invalid/cai/oauth/authorize?x=1","https://claude.com/cai/oauth/authorize?x=1#code","http://claude.com/cai/oauth/authorize?x=1"):
            with self.assertRaises(browser.BrowserError): browser.open_auth_browser(self.config,url,self.account,self.attempt)
        self.spawn.assert_not_called()
        result=self.open()
        marker=(Path(result["browser_profile_dir"]).parent/browser.AUTH_MARKER).read_text()
        self.assertNotIn(self.url,marker)
        self.assertNotIn("SYNTHETIC_SECRET_STATE",json.dumps(result)+marker)
    def test_attended_auto_branch_calls_real_fresh_api_once_and_retains_on_cancel(self):
        class ImmediateThread:
            def __init__(self,target,args=(),daemon=False): self.target,self.args=target,args
            def start(self): self.target(*self.args)
        def pending(_): raise browser.subprocess.TimeoutExpired("synthetic native auth",0.1)
        native=types.SimpleNamespace(wait=pending)
        output=io.BytesIO(("If the browser didn't open, visit: "+self.url+"\n"+"If the browser didn't open, visit: "+self.url+"\n").encode())
        shown=io.StringIO()
        with mock.patch.object(auth_login.threading,"Thread",ImmediateThread),\
             mock.patch.object(auth_login.getpass,"getpass",return_value=":cancel"),\
             contextlib.redirect_stdout(shown):
            code,cancelled,info=auth_login._attended(native,output,io.BytesIO(),io.BytesIO(),self.config,self.account,self.attempt)
        self.assertIsNone(code)
        self.assertTrue(cancelled)
        self.spawn.assert_called_once()
        self.assertTrue(info["browser_launch_requested"])
        self.assertTrue(info["browser_profile_fresh"])
        self.assertFalse(info["browser_process_closed"])
        self.assertEqual(info["browser_attempt_id"],self.attempt)
        self.assertTrue(Path(info["browser_profile_dir"]).exists())
        self.assertNotIn("SYNTHETIC_SECRET_STATE",shown.getvalue()+json.dumps(info))


if __name__=="__main__": unittest.main()
