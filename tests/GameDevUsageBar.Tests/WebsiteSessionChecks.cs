using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameDevUsageBar.Infrastructure;

static class WebsiteSessionChecks
{
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root)
    {
        var profiles=Path.Combine(root,"firefox-profiles");var relative=Path.Combine(profiles,"Profiles","test");var custom=Path.Combine(root,"custom-firefox");
        Directory.CreateDirectory(relative);Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(profiles,"profiles.ini"),$"[Profile0]\nName=Test\nIsRelative=1\nPath=Profiles/test\n[Profile1]\nName=Custom\nIsRelative=0\nPath={custom}\n[InstallTest]\nDefault=Profiles/test\n");
        void Check(bool condition){if(!condition)throw new Exception("Website session check failed.");}
        yield return ("Firefox discovery supports explicit relative and custom profiles without reading cookies",()=>{
            var found=WebsiteSession.Profiles(profiles);
            // Hosted Windows runners may expose TEMP through an 8.3 alias.
            // Compare the canonical paths returned by discovery, not spelling.
            Check(found.Length==2&&StringComparer.OrdinalIgnoreCase.Equals(found[0].Directory,Path.GetFullPath(relative))&&StringComparer.OrdinalIgnoreCase.Equals(found[1].Directory,Path.GetFullPath(custom)));return Task.CompletedTask;
        });
        yield return ("Firefox import isolates website, expiry, path and container with a read-only database",()=>{
            var file=Path.Combine(relative,"cookies.sqlite");Create(file);
            var before=SHA256.HashData(File.ReadAllBytes(file));
            var profile=new FirefoxProfile("Test",relative);var header=WebsiteSession.ImportFirefox("claude",profile);
            Check(header=="path=fixture-billing; session=fixture-claude; parent=fixture-parent");
            Check(WebsiteSession.ImportFirefox("claude",profile)==header);
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))));return Task.CompletedTask;
        });
        yield return ("Website login is pinned and Claude cannot launch Chrome or the default browser",()=>{
            Check(WebsiteSession.LoginPage("claude").Host=="claude.ai"&&WebsiteSession.Browsers("claude").SequenceEqual(new[]{"firefox"}));
            foreach(var browser in new[]{"chrome","edge","default"}){bool rejected=false;try{WebsiteSession.LoginCommand("claude",browser);}catch(InvalidOperationException){rejected=true;}Check(rejected);}
            bool missing=false;try{WebsiteSession.ImportFirefox("claude",new("Empty",custom));}catch(InvalidOperationException){missing=true;}Check(missing);
            Check(WebsiteSession.CookiePathMatches("/settings/billing","/settings")&&!WebsiteSession.CookiePathMatches("/settings/billing","/setting"));return Task.CompletedTask;
        });
    }
    private static void Create(string file)
    {
        var code=sqlite3_open_v2(file,out var db,6,IntPtr.Zero);try {
            if(code!=0)throw new Exception("Fixture database creation failed.");
            var sql="""
                CREATE TABLE moz_cookies(name TEXT,value TEXT,host TEXT,path TEXT,expiry INTEGER,originAttributes TEXT,creationTime INTEGER);
                INSERT INTO moz_cookies VALUES ('session','fixture-claude','claude.ai','/',4070908800,'',1);
                INSERT INTO moz_cookies VALUES ('parent','fixture-parent','.claude.ai','/',4070908800,'',2);
                INSERT INTO moz_cookies VALUES ('path','fixture-billing','claude.ai','/api',4070908800,'',3);
                INSERT INTO moz_cookies VALUES ('expired','excluded','claude.ai','/',1,'',4);
                INSERT INTO moz_cookies VALUES ('container','excluded','claude.ai','/',4070908800,'^userContextId=1',5);
                INSERT INTO moz_cookies VALUES ('unrelated','excluded','example.invalid','/',4070908800,'',6);
                INSERT INTO moz_cookies VALUES ('lookalike','excluded','claude.ai.attacker.invalid','/',4070908800,'',7);
                INSERT INTO moz_cookies VALUES ('host-only-parent','excluded','ai','/',4070908800,'',8);
                INSERT INTO moz_cookies VALUES ('wrong-path','excluded','claude.ai','/wrong',4070908800,'',9);
                """;
            if(sqlite3_exec(db,sql,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero)!=0)throw new Exception("Fixture database setup failed.");
        } finally {if(db!=IntPtr.Zero)sqlite3_close(db);}
    }
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file,out IntPtr db,int flags,IntPtr vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_exec(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,IntPtr callback,IntPtr context,IntPtr error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_close(IntPtr db);
}
