# Optional Claude Code Bridge integration

These files document and test an integration with a separately maintained
Claude Code Bridge. That Bridge is an external dependency; GameDevUsageBar's
installer and portable ZIP do not bundle it, its policy, network guards, pinned
Claude executable, or protected Firefox installation. A normal repository clone
does not provide a working Bridge.

The patches and Python modules here are integration source, not a complete
installable Bridge or a portable Claude login implementation. They require a
compatible maintained Bridge with the reviewed `auth-login` entry, executable
pins and network/browser protection. The application blocks attended login
when that dependency is unavailable. Neither the application nor its installer
automatically applies these patches or installs or changes a Bridge policy.
Installing or adapting the external Bridge requires separate review; copying
these files into an arbitrary Python environment is insufficient.

The local fixture tests import source from an already installed user-level
`claude-code-bridge` skill. They are optional integration checks, not standalone
tests that every clone or release runner can execute. Fixtures use synthetic
account files and mocked Claude, browser and network launch points. The
`192.0.2.10:8080` proxy is an RFC 5737 example used only in fixture data and
expectations; it is not a default proxy and no test connects to it. Some Windows
fixtures exercise byte locks and a harmless, isolated Python child process;
they do not launch Claude, Firefox or actual authentication.

Offline fixture success does not prove browser runtime isolation, successful
authorization, account identity, quota access or billing. Actual sign-in remains
an attended action through the installed, protected Bridge. See
[workflow-auth-login.md](workflow-auth-login.md) for its contract and evidence
boundaries. Local installation evidence and Python caches are excluded from
version control.
