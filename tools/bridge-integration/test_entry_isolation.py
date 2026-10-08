"""Maintained parser with staged auth module, synthetic files and mock Job only."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import unittest
from unittest import mock
import test_auth_login as fixtures
import auth_login


class EntryIsolationTests(unittest.TestCase):
    def setUp(self):
        fixtures.AuthLoginTests.setUp(self)
        scripts=Path.home()/".codex"/"skills"/"claude-code-bridge"/"scripts"
        # Only Python source is loaded; every native creation point is mocked.
        spec=importlib.util.spec_from_file_location("bridge_isolation_fixture",scripts/"bridge.py")
        module=importlib.util.module_from_spec(spec)
        sys.modules[spec.name]=module
        self.addCleanup(lambda:sys.modules.pop(spec.name,None))
        spec.loader.exec_module(module)
        for field in ("DEFAULT_POLICY","PRODUCTION_STATE","CLI","FIREFOX","policy_load","file_hash","WindowsJob"):
            setattr(module,field,getattr(self.bridge,field))
        self.entry=module
    def test_parser_uses_staged_auth_with_profile_and_attempt_no_probe_model(self):
        result=Path(self.temp.name)/"parser-receipt.json"
        with mock.patch.object(self.entry,"safe_probe",side_effect=AssertionError("No CLI probe")),\
             mock.patch.object(self.entry,"preflight_internal",side_effect=AssertionError("No auth-status")),\
             mock.patch.object(self.entry,"run_task",side_effect=AssertionError("No model")),\
             mock.patch.object(auth_login,"_attended",return_value=(0,False,{"browser_profile_fresh":True,"browser_profile_retained":True,"browser_process_closed":False,"browser_tree_closed_verified":False})) as attended,\
             contextlib.redirect_stdout(io.StringIO()):
            code=self.entry.main(["auth-login","--config-dir",str(self.profile),"--result",str(result)])
        self.assertEqual(code,0)
        receipt=json.loads(result.read_text())
        self.assertEqual(attended.call_args.args[-2],self.profile.resolve())
        self.assertEqual(attended.call_args.args[-1],receipt["attempt_id"])
        self.assertEqual(self.bridge.commands,[[str(self.cli),"--safe-mode","auth","login","--claudeai"]])
        self.assertTrue(receipt["process_closed"])
        self.assertFalse(receipt["browser_process_closed"])
        self.assertFalse(receipt["account_status_verified"])
    def test_parser_keeps_unknown_browser_outcome_blocks_replay(self):
        with mock.patch.object(auth_login,"_attended",return_value=(0,False,{"browser_launch_unknown":True})),contextlib.redirect_stdout(io.StringIO()):
            code=self.entry.main(["auth-login","--config-dir",str(self.profile)])
        self.assertEqual(code,3)
        state=json.loads((self.profile/auth_login.LOGIN_STATE).read_text())
        self.assertEqual(state["status"],"unknown")
        with contextlib.redirect_stdout(io.StringIO()): self.entry.main(["auth-login","--config-dir",str(self.profile)])
        self.assertEqual(len(self.bridge.commands),1)
        self.assertEqual(json.loads((self.profile/auth_login.LOGIN_STATE).read_text()),state)
    def test_parser_no_native_model_or_unapproved_browser_flags(self):
        with contextlib.redirect_stderr(io.StringIO()):
            for flag in ("--model","--print","--console","--no-browser"):
                with self.assertRaises(SystemExit): self.entry.parser().parse_args(["auth-login","--config-dir",str(self.profile),flag])
        self.assertEqual(self.bridge.commands,[])


if __name__=="__main__": unittest.main()
