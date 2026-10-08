"""Apply staged patches to local copies and exercise the maintained parser.

No actual CLI, browser, guard, account lookup or authentication is permitted.
"""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import unittest
from unittest import mock

import test_auth_login as fixtures
import auth_login as login

HERE = Path(__file__).resolve().parent
INSTALLED = Path.home()/".codex"/"skills"/"claude-code-bridge"


def apply_patch(source, patch):
    original=source.splitlines(keepends=True)
    output=[]
    cursor=0
    hunk=False
    for line in patch.splitlines(keepends=True):
        if line.startswith(("--- ","+++ ")): continue
        match=re.match(r"@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@",line)
        if match:
            start=int(match.group(1))-1
            output.extend(original[cursor:start])
            cursor=start
            hunk=True
        elif hunk and line[0] in " -+":
            content=line[1:]
            if line[0] in " -":
                assert original[cursor]==content, "Patch baseline mismatch"
                cursor+=1
            if line[0] in " +": output.append(content)
        elif line.startswith("\\ No newline"): pass
        else: raise AssertionError("Unexpected patch content")
    output.extend(original[cursor:])
    return "".join(output)


class BridgeEntryTests(unittest.TestCase):
    def setUp(self):
        fixtures.AuthLoginTests.setUp(self)
        scripts=Path(self.temp.name)/"scripts"
        scripts.mkdir()
        for name,patch in (("bridge.py","bridge-auth-login.patch"),("browser.py","browser-auth-login.patch")):
            installed=(INSTALLED/"scripts"/name).read_text(encoding="utf-8")
            staged=HERE/name
            if name=="bridge.py" and 'import auth_login' in installed and 'sub.add_parser("auth-login"' in installed:
                candidate=installed  # The maintained installed entry is already routed.
            elif name=="browser.py" and staged.is_file():
                candidate=staged.read_text(encoding="utf-8")
            elif name=="browser.py" and 'environment=None' in installed:
                candidate=installed  # Do not apply an already-installed patch again.
            else:
                candidate=apply_patch(installed,(HERE/patch).read_text(encoding="utf-8"))
            target=scripts/name
            target.write_text(candidate,encoding="utf-8",newline="\n")
            compile(candidate,str(target),"exec")
        name="bridge_auth_entry_fixture"
        spec=importlib.util.spec_from_file_location(name,scripts/"bridge.py")
        module=importlib.util.module_from_spec(spec)
        sys.modules[name]=module
        self.addCleanup(lambda:sys.modules.pop(name,None))
        spec.loader.exec_module(module)
        self.native_job=module.WindowsJob
        for field in ("DEFAULT_POLICY","PRODUCTION_STATE","CLI","FIREFOX","policy_load","file_hash","WindowsJob"):
            setattr(module,field,getattr(self.bridge,field))
        self.entry=module
    def test_parser_runs_protected_auth_entry_without_probes_or_model(self):
        result=Path(self.temp.name)/"parser-result.json"
        output=io.StringIO()
        with mock.patch.object(self.entry,"safe_probe",side_effect=AssertionError("No probes")),\
             mock.patch.object(self.entry,"preflight_internal",side_effect=AssertionError("No auth status")),\
             mock.patch.object(self.entry,"run_task",side_effect=AssertionError("No model request")),\
             mock.patch.object(sys,"stdout",output):
            code=self.entry.main(["auth-login","--config-dir",str(self.profile),"--result",str(result)])
        self.assertEqual(code,0)
        receipt=json.loads(result.read_text())
        self.assertEqual(receipt["status"],"completed_unverified")
        self.assertTrue(receipt["process_closed"])
        self.assertEqual(self.bridge.commands,[[str(self.cli),"--safe-mode","auth","login","--claudeai"]])
    def test_parser_optional_result_and_failure_code(self):
        with mock.patch.object(login,"_attended",return_value=(1,False,{"browser_launch_unknown":False})),contextlib.redirect_stdout(io.StringIO()):
            code=self.entry.main(["auth-login","--config-dir",str(self.profile)])
        self.assertEqual(code,3)
        records=list(self.profile.glob(".bridge-auth-result-*.json"))
        self.assertEqual(len(records),1)
        self.assertEqual(json.loads(records[0].read_text())["status"],"failed")
    def test_parser_cannot_accept_native_prompt_or_extra_flags(self):
        with contextlib.redirect_stderr(io.StringIO()):
            for forbidden in ("--console","--model","--no-browser","--print"):
                with self.assertRaises(SystemExit): self.entry.parser().parse_args(["auth-login","--config-dir",str(self.profile),forbidden])
        self.assertEqual(self.bridge.commands,[])
    def test_staged_browser_accepts_sanitized_environment_without_parent_secrets(self):
        spec=importlib.util.spec_from_file_location("auth_browser_fixture",Path(self.temp.name)/"scripts"/"browser.py")
        module=importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        checks={"executable":str(self.firefox),"profile_dir":str(self.profile),"proxy_ip":"192.0.2.10","proxy_port":8080,"guarded_application_count":14}
        with mock.patch.object(module,"status",return_value=checks),\
             mock.patch.object(module.subprocess,"Popen",return_value=type("Fake",(),{"pid":1})()) as spawn,\
             mock.patch.dict(module.os.environ,{"ANTHROPIC_API_KEY":"fixture-secret","BROWSER":"wrong-browser"}):
            module.open_browser({},"https://claude.com/cai/oauth/authorize?code=true&state=fixture",environment=login.browser_environment())
        passed=spawn.call_args.kwargs["env"]
        self.assertNotIn("fixture-secret",str(passed))
        self.assertEqual(spawn.call_args.args[0][0],str(self.firefox))
        self.assertEqual(passed["NO_PROXY"],"")
    @unittest.skipUnless(os.name=="nt","Windows byte-lock fixture")
    def test_readonly_lock_and_existing_msvcrt_lock_contend_both_directions(self):
        self.patches[2].stop()  # Use actual LockFileEx only on this synthetic file.
        state=Path(self.temp.name)/"synthetic-state"
        state.mkdir()
        lock_path=state/"bridge.lock"
        lock_path.write_bytes(b"0")
        with self.entry.locked(state):
            with self.assertRaises(login.LoginBlocked):
                with login.read_only_serial_lock(lock_path): self.fail("Read-only lock bypassed the existing writer lock")
        with login.read_only_serial_lock(lock_path):
            with self.assertRaises(self.entry.BridgeBusy):
                with self.entry.locked(state): self.fail("Existing lock bypassed the read-only login lock")
        self.assertEqual(lock_path.read_bytes(),b"0")
    @unittest.skipUnless(os.name=="nt","Windows Job fixture")
    def test_actual_windows_job_transports_pipes_and_observes_closure(self):
        # Explicit bounded fixture child only, with no CLI, browser, subprocess,
        # network, credentials, user imports or application-file writes.
        with contextlib.ExitStack() as stack:
            in_read,in_write=os.pipe()
            out_read,out_write=os.pipe()
            err_read,err_write=os.pipe()
            stdin_read=stack.enter_context(os.fdopen(in_read,"rb",buffering=0))
            stdin_write=stack.enter_context(os.fdopen(in_write,"wb",buffering=0))
            stdout_read=stack.enter_context(os.fdopen(out_read,"rb",buffering=0))
            stdout_write=stack.enter_context(os.fdopen(out_write,"wb",buffering=0))
            stderr_read=stack.enter_context(os.fdopen(err_read,"rb",buffering=0))
            stderr_write=stack.enter_context(os.fdopen(err_write,"wb",buffering=0))
            script="import sys; data=sys.stdin.buffer.readline(); sys.stdout.buffer.write(b'fixture-stdout:'+data); sys.stdout.flush(); sys.stderr.buffer.write(b'fixture-stderr\\n'); sys.stderr.flush()"
            command=[sys.executable,"-I","-S","-B","-c",script]
            process=self.native_job(command,{"SystemRoot":os.environ.get("SystemRoot",r"C:\Windows")},Path(self.temp.name),stdin_read,stdout_write,stderr_write,process_limit=1)
            try:
                process.resume()
                stdin_write.write(b"fixture-input\n")
                stdin_write.flush()
                self.assertEqual(process.wait(5),0)
                self.assertTrue(process.terminate())
                self.assertTrue(process.tree_exit_observed)
                stdout_write.close()
                stderr_write.close()
                self.assertEqual(stdout_read.read(),b"fixture-stdout:fixture-input\n")
                self.assertEqual(stderr_read.read(),b"fixture-stderr\n")
            finally:
                process.terminate()
                process.close()


if __name__=="__main__": unittest.main()
