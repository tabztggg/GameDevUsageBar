using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameDevUsageBar.Infrastructure;

public sealed record FirefoxProfile(string Name,string Directory);

// User-initiated import only. No browser launch/debugging, profile copy, or credential logging.
public static class WebsiteSession
{
    public static Uri LoginPage(string provider)=>provider switch {
        "claude"=>new("https://claude.ai/settings/usage"),
        _=>throw new InvalidOperationException("Website login is not supported for this provider.")
    };
    public static string[] Browsers(string provider)=>provider=="claude" ? ["firefox"] : [];
    public static FirefoxProfile[] Profiles(string? root=null)
    {
        root??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Mozilla","Firefox");
        var ini=Path.Combine(root,"profiles.ini");if(!File.Exists(ini))return [];
        var profiles=new List<FirefoxProfile>();var section="";var fields=new Dictionary<string,string>();
        void Add()
        {
            if(!section.StartsWith("Profile",StringComparison.Ordinal)||!fields.TryGetValue("Path",out var path))return;
            var directory=Path.GetFullPath(fields.GetValueOrDefault("IsRelative","1")=="1"?Path.Combine(root,path):path);
            if(!System.IO.Directory.Exists(directory))return;
            profiles.Add(new(fields.GetValueOrDefault("Name",Path.GetFileName(directory)),directory));
        }
        foreach(var raw in File.ReadLines(ini).Take(4096))
        {
            var line=raw.Trim();if(line.StartsWith('[')&&line.EndsWith(']')){Add();section=line[1..^1];fields.Clear();}
            else {var index=line.IndexOf('=');if(index>0)fields[line[..index]]=line[(index+1)..];}
        }
        Add();return profiles.DistinctBy(p=>p.Directory,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static bool CookiePathMatches(string request,string cookie)=>request==cookie || (request.StartsWith(cookie,StringComparison.Ordinal)&&(cookie.EndsWith('/')||(request.Length>cookie.Length&&request[cookie.Length]=='/')));
    public static string ImportFirefox(string provider,FirefoxProfile profile)
    {
        var page=LoginPage(provider);var path="/api/organizations/";
        var parent=".claude.ai";
        var file=Path.Combine(profile.Directory,"cookies.sqlite");if(!File.Exists(file))throw new InvalidOperationException("No website cookies found in this Firefox profile. Sign in first, then import again.");
        IntPtr db=IntPtr.Zero,statement=IntPtr.Zero;
        try
        {
            if(Native.sqlite3_open_v2(file,out db,1,IntPtr.Zero)!=0)throw new IOException();
            Native.sqlite3_busy_timeout(db,1500);
            const string sql="SELECT name,value,path FROM moz_cookies WHERE host IN (?1,?2,?3) AND (expiry=0 OR expiry>?4) AND originAttributes='' ORDER BY length(path) DESC, creationTime ASC LIMIT 128";
            if(Native.sqlite3_prepare_v2(db,sql,-1,out statement,IntPtr.Zero)!=0)throw new IOException();
            Native.sqlite3_bind_text(statement,1,page.Host,-1,new IntPtr(-1));Native.sqlite3_bind_text(statement,2,"."+page.Host,-1,new IntPtr(-1));Native.sqlite3_bind_text(statement,3,parent,-1,new IntPtr(-1));
            Native.sqlite3_bind_int64(statement,4,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var values=new List<string>();int code;
            while((code=Native.sqlite3_step(statement))==100)
            {
                string Text(int column)=>Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement,column))??"";
                if(!CookiePathMatches(path,Text(2)))continue;
                var name=Text(0);var value=Text(1);
                if(!Regex.IsMatch(name,@"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\z")||value.Any(c=>char.IsControl(c)||c==';')||value.Length>8192)throw new IOException();
                values.Add(name+"="+value);
            }
            if(code!=101)throw new IOException();
            if(values.Count==0)throw new InvalidOperationException("No website cookies found in this Firefox profile. Sign in first, then import again.");
            return ProviderQueryClient.Cookie(string.Join("; ",values));
        }
        catch(InvalidOperationException){throw;}
        catch {throw new IOException("Could not read Firefox cookies. Close Firefox and try importing again, or paste the Cookie header manually.");}
        finally {if(statement!=IntPtr.Zero)Native.sqlite3_finalize(statement);if(db!=IntPtr.Zero)Native.sqlite3_close(db);}
    }
    public static ProcessStartInfo LoginCommand(string provider,string browser,FirefoxProfile? profile=null)
    {
        if(!Browsers(provider).Contains(browser))throw new InvalidOperationException("This browser is not available for this provider.");
        var url=LoginPage(provider).AbsoluteUri;
        if(browser=="default")return new(url){UseShellExecute=true};
        var relative=browser switch {"firefox"=>@"Mozilla Firefox\firefox.exe","chrome"=>@"Google\Chrome\Application\chrome.exe","edge"=>@"Microsoft\Edge\Application\msedge.exe",_=>throw new InvalidOperationException()};
        var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)};
        var executable=roots.Select(root=>Path.Combine(root,relative)).FirstOrDefault(File.Exists)??throw new FileNotFoundException("Selected browser was not found. Choose an installed browser.");
        var command=new ProcessStartInfo(executable){UseShellExecute=false};
        if(browser=="firefox"&&profile is not null){command.ArgumentList.Add("-profile");command.ArgumentList.Add(profile.Directory);}
        command.ArgumentList.Add(url);return command;
    }
    private static class Native
    {
        private const string Dll="winsqlite3.dll";
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file,out IntPtr db,int flags,IntPtr vfs);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_prepare_v2(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,int bytes,out IntPtr statement,IntPtr tail);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_bind_text(IntPtr statement,int index,[MarshalAs(UnmanagedType.LPUTF8Str)] string value,int bytes,IntPtr destructor);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_bind_int64(IntPtr statement,int index,long value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_busy_timeout(IntPtr db,int ms);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_step(IntPtr statement);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern IntPtr sqlite3_column_text(IntPtr statement,int column);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int sqlite3_close(IntPtr db);
    }
}
