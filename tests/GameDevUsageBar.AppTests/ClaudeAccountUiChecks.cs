using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

// Actual WPF buttons and host persistence, with an injected login outcome and
// synthetic credential homes only. No CLI, browser, provider or model is called.
internal static class ClaudeAccountUiChecks
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var results=new List<object>();var failed=0;
        async Task Check(string name,Func<Task> test)
        {
            try{await test();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;results.Add(new{name,status="FAIL",error=error.ToString()});Console.WriteLine("FAIL "+name+" "+error.Message);}
        }
        try
        {
            await Check("Claude Add validates a required custom name, focus and translated instructions",async()=>{
                await using var fixture=await Fixture.Create(root,"validation");using var manager=new Manager(fixture,"claude");
                var window=manager.Window;var label=Named<TextBox>(window,"NewLabel");var add=Named<Button>(window,"AddButton");
                var loginCalls=0;fixture.Host.ClaudeLoginFixture=(_,_)=>{loginCalls++;return Task.FromResult(new ClaudeLoginOutcome("failed",true));};
                foreach(var language in new[]{"zh-CN","en-US"})
                {
                    Localizer.SetLanguage(language);await Idle();label.Text="   ";Click(add);await Idle();
                    Assert(fixture.Host.GetAccounts("claude").Count==1&&loginCalls==0,"Blank label created or launched an account");
                    Assert(Named<TextBlock>(window,"Feedback").Text==Localizer.T("Enter an account name before signing in."),"Required-name feedback is missing or untranslated");
                    Assert(ReferenceEquals(FocusManager.GetFocusedElement(window),label),"Required-name validation did not return logical keyboard focus to the label");
                    Assert(add.Content as string==Localizer.T("Add Claude account")&&AutomationProperties.GetName(label)==Localizer.T("Account name (required)"),"Claude button or accessible label was not translated");
                    Assert(Named<TextBlock>(window,"NativeIntro").Text==Localizer.T("Adding or signing in to an account opens a CLI terminal with a fresh Firefox profile. Complete the authorization with the intended Claude account. Other browser logins are not reused."),"Fresh Firefox/manual authorization boundary is missing");
                    Assert(Named<TextBlock>(window,"NativeBoundary").Text==Localizer.T("The name is your own label, so check the account on the authorization page. Usage is read after login completes. Edit account can rename the label."),"Account-label/actual-account/query/rename explanation is missing");
                    Assert(label.IsTabStop&&add.IsTabStop&&KeyboardNavigation.GetTabIndex(label)<KeyboardNavigation.GetTabIndex(add),"Name and Add do not have a usable keyboard tab order");
                    HorizontalFit(window);Render(window,Path.Combine(root,$"claude-account-add-{language}-150.png"));
                }
                Assert(fixture.Host.Queries.Events.Count==0&&fixture.Adapters.All(a=>a.Calls.Count==0),"Validation contacted a provider");
            });
            await Check("Claude profile creation failure preserves account rows and settings without launching login",async()=>{
                await using var fixture=await Fixture.Create(root,"profile-parent-file");await fixture.Host.SaveAsync(fixture.Host.GetActiveAccount("claude"));
                var accounts=fixture.Host.Accounts.ToArray();var settings=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));
                var parent=Path.Combine(fixture.Host.Root,"claude-profiles");var obstacle="Synthetic fixture occupies the profile parent path.";await File.WriteAllTextAsync(parent,obstacle);var calls=0;
                fixture.Host.ClaudeLoginFixture=(_,_)=>{calls++;return Task.FromResult(new ClaudeLoginOutcome("failed",true));};using var manager=new Manager(fixture,"claude");
                Named<TextBox>(manager.Window,"NewLabel").Text="Blocked profile fixture";Click(Named<Button>(manager.Window,"AddButton"));await Until(()=>Named<Button>(manager.Window,"AddButton").IsEnabled);await Idle();
                Assert(fixture.Host.Accounts.SequenceEqual(accounts)&&(await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(settings),"Profile directory failure persisted a partial or duplicate account row");
                Assert(calls==0&&fixture.Adapters.All(a=>a.Calls.Count==0)&&fixture.Host.Queries.Events.Count==0,"Profile creation failure launched sign-in or queried a provider");
                Assert((await File.ReadAllTextAsync(parent))==obstacle&&Named<ListBox>(manager.Window,"AccountList").Items.Count==1&&Named<TextBox>(manager.Window,"NewLabel").Text=="Blocked profile fixture","Profile creation failure changed the occupied path, row count or entered label");
                Assert(Named<TextBlock>(manager.Window,"Feedback").Text==Localizer.T("The account operation could not finish. No credentials were logged."),"Profile creation failure did not show handled feedback");
            });
            await Check("Claude Add creates one pending isolated slot, waits for login, then binds and refreshes that slot",async()=>{
                await using var fixture=await Fixture.Create(root,"success");using var manager=new Manager(fixture,"claude");var window=manager.Window;
                var original=fixture.Host.GetActiveAccount("claude");var defaultBytes=await File.ReadAllBytesAsync(fixture.Native.PathFor("claude"));
                var gate=new TaskCompletionSource<ClaudeLoginOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);AccountConfig? pending=null;var calls=0;
                fixture.Host.ClaudeLoginFixture=(config,_)=>{pending=config;calls++;return gate.Task;};
                Named<TextBox>(window,"NewLabel").Text="  Writer account fixture  ";Click(Named<Button>(window,"AddButton"));
                await Until(()=>pending is not null);var pendingAccount=pending??throw new InvalidOperationException("Login did not receive its pending account");var slot=pendingAccount.SlotId;var directory=pendingAccount.ClaudeConfigDirectory;
                Assert(directory is not null&&Path.IsPathFullyQualified(directory),"New account has no stable isolated config path");
                Assert(pendingAccount.Label=="Writer account fixture"&&!pendingAccount.Enabled&&!pendingAccount.HasUsableCredential&&pendingAccount.NativeAuthRef is null,"Pending account has wrong label or inherited credentials");
                Assert(fixture.Host.GetAccounts("claude").Count==2&&fixture.Host.GetActiveAccount("claude").SlotId==original.SlotId,"Add changed the displayed account or created duplicate slots");
                Assert(Named<ListBox>(window,"AccountList").SelectedItem is CardModel selected&&selected.SlotId==slot,"New pending row was not selected");
                foreach(var name in new[]{"AddButton","NewLabel","AccountList","SelectButton","EditButton","DeleteButton","RefreshButton","LoginButton","CaptureButton","SwitchButton"})Assert(!Named<UIElement>(window,name).IsEnabled,"A control stayed enabled while login was pending: "+name);
                Assert(Named<TextBlock>(window,"Feedback").Text==Localizer.T("Opening an isolated CLI login with a fresh Firefox profile. Authorize the intended Claude account in the opened terminal; this account stays pending until login is verified."),"Pending login has no progress feedback");
                HorizontalFit(window);Render(window,Path.Combine(root,"claude-account-pending-en-US-150.png"));
                Click(Named<Button>(window,"AddButton"));Click(Named<Button>(window,"LoginButton"));Click(Named<Button>(window,"CaptureButton"));await Idle();
                Assert(calls==1&&fixture.Host.GetAccounts("claude").Count==2&&fixture.Claude.Calls.Count==0,"A busy click relaunched login, duplicated the slot or queried before login completion");
                await WriteProfile(pendingAccount);gate.SetResult(new("completed_unverified",true));await Until(()=>Named<Button>(window,"AddButton").IsEnabled);
                var ready=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==slot);
                Assert(ready.Enabled&&ready.HasUsableCredential&&ready.NativeIdentity is not null&&ready.ClaudeConfigDirectory==directory&&ready.NativeAuthRef is null,"Successful login did not bind the same isolated slot");
                Assert(fixture.Claude.Calls.GetValueOrDefault(slot)==1&&fixture.Claude.Calls.Count==1,"Completed login did not refresh only the new slot");
                Assert(Named<Button>(window,"LoginButton").IsVisible&&Named<Button>(window,"LoginButton").IsEnabled&&Named<Button>(window,"CaptureButton").Content as string==Localizer.T("Read account login")&&!Named<Button>(window,"SwitchButton").IsEnabled,"Isolated slot has wrong action capabilities");
                foreach(var language in new[]{"zh-CN","en-US"}){Localizer.SetLanguage(language);await Idle();HorizontalFit(window);Render(window,Path.Combine(root,$"claude-account-ready-{language}-150.png"));}
                Click(Named<Button>(window,"SwitchButton"));await Idle();
                Assert((await File.ReadAllBytesAsync(fixture.Native.PathFor("claude"))).SequenceEqual(defaultBytes)&&fixture.Host.GetActiveAccount("claude").SlotId==original.SlotId,"Isolated login or disabled switch changed the default login/display selection");
                Assert(!((await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root,"settings.json"))).Contains("nonfunctional-access",StringComparison.Ordinal)),"Synthetic token leaked into account settings");
                Assert(fixture.Host.Queries.Events.Count==0,"Fixture login sent a provider request");
                var textBox=Named<TextBox>(window,"NewLabel");FocusManager.SetFocusedElement(window,textBox);textBox.MoveFocus(new( FocusNavigationDirection.Next));
                Assert(ReferenceEquals(FocusManager.GetFocusedElement(window),Named<Button>(window,"AddButton")),"Keyboard Tab from account name did not reach Add");
            });
            await Check("Claude failed and busy logins keep the same named slot for explicit retry",async()=>{
                foreach(var status in new[]{"failed","blocked","busy"})
                {
                    var nativeBusy=status=="busy";await using var fixture=await Fixture.Create(root,status,()=>nativeBusy);using var manager=new Manager(fixture,"claude");var window=manager.Window;var calls=0;Guid firstSlot=Guid.Empty;
                    fixture.Host.ClaudeLoginFixture=(config,_)=>{calls++;firstSlot=config.SlotId;return Task.FromResult(new ClaudeLoginOutcome(status,true));};
                    var initialCalls=status=="busy"?0:1;Named<TextBox>(window,"NewLabel").Text=status+" named fixture";Click(Named<Button>(window,"AddButton"));await Until(()=>fixture.Host.GetAccounts("claude").Count==2&&calls==initialCalls&&Named<Button>(window,"AddButton").IsEnabled);
                    var pending=fixture.Host.GetAccounts("claude").Single(a=>a.ClaudeConfigDirectory is not null);firstSlot=pending.SlotId;
                    Assert(!pending.Enabled&&!pending.HasUsableCredential&&fixture.Claude.Calls.Count==0&&fixture.Host.GetAccounts("claude").Count==2,"Failed/busy login removed its pending record or queried it");
                    Assert(Named<Button>(window,"LoginButton").IsEnabled&&!string.IsNullOrWhiteSpace(Named<TextBlock>(window,"Feedback").Text),"Failed/busy outcome offers no explicit retry or feedback");
                    if(status=="failed")
                    {
                        Named<TextBox>(window,"NewLabel").Text=pending.Label;Click(Named<Button>(window,"AddButton"));await Until(()=>calls==2&&Named<Button>(window,"AddButton").IsEnabled);initialCalls=2;
                        Assert(fixture.Host.GetAccounts("claude").Count==2&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==firstSlot).ClaudeConfigDirectory==pending.ClaudeConfigDirectory,"Adding the same pending name duplicated its account or config path");
                    }
                    nativeBusy=false;
                    fixture.Host.ClaudeLoginFixture=async(config,_)=>{Assert(config.SlotId==firstSlot&&config.Label==pending.Label,"Retry selected another account");calls++;await WriteProfile(config);return new("completed_unverified",true);};
                    Click(Named<Button>(window,"LoginButton"));await Until(()=>calls==initialCalls+1&&Named<Button>(window,"AddButton").IsEnabled);
                    Assert(fixture.Host.GetAccounts("claude").Count==2&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==firstSlot).Enabled&&fixture.Claude.Calls.GetValueOrDefault(firstSlot)==1,"Explicit retry duplicated the record or failed to bind/query it");
                }
            });
            await Check("Claude UNKNOWN stays blocked while an explicit changed-profile read binds current credentials",async()=>{
                var nativeBusy=false;await using var fixture=await Fixture.Create(root,"unknown",()=>nativeBusy);using var manager=new Manager(fixture,"claude");var window=manager.Window;var calls=0;AccountConfig? pending=null;
                fixture.Host.ClaudeLoginFixture=(config,_)=>{calls++;pending=config;return Task.FromResult(new ClaudeLoginOutcome("unknown",false));};
                Named<TextBox>(window,"NewLabel").Text="Unknown fixture";Click(Named<Button>(window,"AddButton"));await Until(()=>calls==1&&Named<Button>(window,"AddButton").IsEnabled);
                Assert(!Named<Button>(window,"LoginButton").IsEnabled&&Named<Button>(window,"CaptureButton").IsEnabled&&fixture.Claude.Calls.Count==0,"UNKNOWN allows login replay or queried an unverified record");
                Click(Named<Button>(window,"LoginButton"));await Idle();Assert(calls==1,"UNKNOWN login was replayed by the disabled button");
                // The injected guard represents an unresolved active CLI; no real
                // process inventory or credential location is accessed here.
                await WriteProfile(pending!);nativeBusy=true;
                Click(Named<Button>(window,"CaptureButton"));await Until(()=>Named<Button>(window,"AddButton").IsEnabled);
                Assert(!fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending!.SlotId).Enabled&&calls==1,"Reading an unresolved login enabled or relaunched it");
                Assert(!Named<Button>(window,"LoginButton").IsEnabled,"A blocked local read cleared UNKNOWN");
                nativeBusy=false;Click(Named<Button>(window,"CaptureButton"));await Until(()=>Named<Button>(window,"AddButton").IsEnabled);
                Assert(calls==1&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending!.SlotId).Enabled&&!Named<Button>(window,"LoginButton").IsEnabled&&fixture.Claude.Calls.GetValueOrDefault(pending!.SlotId)==1,"Explicit changed-profile read did not bind/query without preserving the UNKNOWN login block");
                Assert(Named<TextBlock>(window,"Feedback").Text==Localizer.T("The current login was read, but the earlier sign-in outcome is unresolved. Do not repeat sign-in."),"Local read incorrectly claimed to settle the earlier UNKNOWN attempt");
                using var reopened=new Manager(fixture,"claude");Named<ListBox>(reopened.Window,"AccountList").SelectedItem=fixture.Hub.GetAccountModels("claude").Single(a=>a.SlotId==pending!.SlotId);await Idle();
                Assert(!Named<Button>(reopened.Window,"LoginButton").IsEnabled,"Reopening the manager lost the host UNKNOWN block");
                Named<TextBox>(reopened.Window,"NewLabel").Text=pending!.Label;Click(Named<Button>(reopened.Window,"AddButton"));await Until(()=>Named<Button>(reopened.Window,"AddButton").IsEnabled);
                Assert(calls==1&&fixture.Host.GetAccounts("claude").Count==2&&!Named<Button>(reopened.Window,"LoginButton").IsEnabled,"Adding an UNKNOWN account name created an alias or replayed sign-in");
            });
            await Check("A maintained UNKNOWN profile receipt survives host restart and local read keeps sign-in blocked",async()=>{
                string folder;AccountConfig pending;byte[] receipt;
                await using(var before=await Fixture.Create(root,"unknown-restart"))
                {
                    pending=await before.Host.AddClaudeAccountAsync("Durable unknown fixture");folder=before.Host.Root;await WriteProfile(pending,"durable-new-fixture");
                    receipt=JsonSerializer.SerializeToUtf8Bytes(new{schema_version=1,slot_id=pending.SlotId.ToString("D"),config_dir_sha256=ProtectedClaudeLoginLauncher.DirectoryHash(pending.ClaudeConfigDirectory!),status="unknown",process_closed=true,cli_started=true,credential_changed=true,credential_change_verified=true});
                    await File.WriteAllBytesAsync(Path.Combine(pending.ClaudeConfigDirectory!,".bridge-auth-login-state.json"),receipt);
                    Assert(before.Host.IsClaudeLoginUnknown(pending.SlotId)&&!before.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId).Enabled,"Maintained UNKNOWN receipt did not block its pending profile");
                }
                await using var after=await Fixture.Reopen(folder);var calls=0;after.Host.ClaudeLoginFixture=(_,_)=>{calls++;return Task.FromResult(new ClaudeLoginOutcome("failed",true));};
                Assert(after.Host.IsClaudeLoginUnknown(pending.SlotId),"Host restart lost the durable UNKNOWN receipt");
                var blocked=await after.Host.LoginClaudeAccountAsync(pending.SlotId);Assert(blocked.Status==NativeAccountStatus.Unknown&&calls==0,"Durable UNKNOWN called the login launcher after host restart");
                var read=await after.Host.CompleteClaudeAccountLoginAsync(pending.SlotId);var bound=after.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId);
                Assert(read.Status==NativeAccountStatus.Unknown&&read.MessageKey=="The current login was read, but the earlier sign-in outcome is unresolved. Do not repeat sign-in."&&bound.Enabled&&bound.HasUsableCredential&&bound.NativeIdentity is not null,"Explicit post-restart read failed to bind the verified changed credential while preserving UNKNOWN");
                Assert(after.Claude.Calls.GetValueOrDefault(pending.SlotId)==1&&calls==0&&after.Host.Queries.Events.Count==0,"Post-restart local read relaunched sign-in or queried a real provider");
                Assert((await File.ReadAllBytesAsync(Path.Combine(pending.ClaudeConfigDirectory!,".bridge-auth-login-state.json"))).SequenceEqual(receipt),"Local read rewrote the unresolved maintained receipt");
                using var manager=new Manager(after,"claude");Named<ListBox>(manager.Window,"AccountList").SelectedItem=after.Hub.GetAccountModels("claude").Single(a=>a.SlotId==pending.SlotId);await Idle();
                Assert(!Named<Button>(manager.Window,"LoginButton").IsEnabled&&Named<Button>(manager.Window,"CaptureButton").IsEnabled,"Reopened profile manager offered sign-in for a durable UNKNOWN attempt");
            });
            await Check("An existing enabled Claude profile pauses the binding callback during login and restores the new binding",async()=>{
                await using var fixture=await Fixture.Create(root,"binding-pause");var pending=await fixture.Host.AddClaudeAccountAsync("Renewal guard fixture");await WriteProfile(pending,"old-binding-fixture");
                Assert((await fixture.Host.CompleteClaudeAccountLoginAsync(pending.SlotId)).Status==NativeAccountStatus.Captured,"Initial synthetic binding failed");
                var original=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId);Assert(CurrentClaudeBinding(fixture.Host,original),"Enabled profile binding was not eligible before login");var calls=0;
                fixture.Host.ClaudeLoginFixture=async(config,_)=>{calls++;Assert(config.Enabled&&config.NativeIdentity==original.NativeIdentity&&!CurrentClaudeBinding(fixture.Host,config),"Existing enabled profile remained eligible for renewal during attended login");await WriteProfile(config,"new-binding-fixture");return new("completed_unverified",true);};
                var result=await fixture.Host.LoginClaudeAccountAsync(original.SlotId);var updated=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==original.SlotId);
                Assert(calls==1&&result.Status==NativeAccountStatus.Captured&&updated.NativeIdentity!=original.NativeIdentity&&CurrentClaudeBinding(fixture.Host,updated)&&!CurrentClaudeBinding(fixture.Host,original),"New binding was not restored after login or stale binding remained eligible");
                Assert(fixture.Claude.Calls.GetValueOrDefault(original.SlotId)==2&&fixture.Host.Queries.Events.Count==0,"Binding guard fixture performed unexpected refreshes or real maintenance/provider work");
            });
            await Check("Duplicate Claude credential identity is rejected across slots while corrected retry and same-slot reauth remain available",async()=>{
                foreach(var source in new[]{"local-oauth","saved-oauth"})
                {
                    await using var fixture=await Fixture.Create(root,"duplicate-"+source);AccountConfig first;
                    if(source=="local-oauth")
                    {
                        first=await fixture.Host.AddClaudeAccountAsync("First account fixture");await WriteProfile(first,"shared-identity-fixture");Assert((await fixture.Host.CompleteClaudeAccountLoginAsync(first.SlotId)).Succeeded,"Initial local profile binding failed");
                    }
                    else
                    {
                        first=fixture.Host.GetActiveAccount("claude");await File.WriteAllBytesAsync(fixture.Native.PathFor("claude"),Credential("shared-identity-fixture"));Assert((await fixture.Host.CaptureCurrentLoginAsync("claude",first.SlotId)).Succeeded,"Initial saved login binding failed");
                    }
                    first=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==first.SlotId);var pending=await fixture.Host.AddClaudeAccountAsync("Second label fixture");var accounts=fixture.Host.Accounts.ToArray();var settings=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));var defaultLogin=await File.ReadAllBytesAsync(fixture.Native.PathFor("claude"));var calls=0;
                    fixture.Host.ClaudeLoginFixture=async(config,_)=>{calls++;await WriteProfile(config,"shared-identity-fixture");return new("completed_unverified",true);};
                    using var manager=new Manager(fixture,"claude");Named<ListBox>(manager.Window,"AccountList").SelectedItem=fixture.Hub.GetAccountModels("claude").Single(a=>a.SlotId==pending.SlotId);Click(Named<Button>(manager.Window,"LoginButton"));await Until(()=>calls==1&&Named<Button>(manager.Window,"AddButton").IsEnabled);
                    Assert(fixture.Host.Accounts.SequenceEqual(accounts)&&(await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(settings)&&!fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId).Enabled,"Duplicate identity changed an account binding or settings");
                    Assert(fixture.Claude.Calls.GetValueOrDefault(pending.SlotId)==0&&Named<Button>(manager.Window,"LoginButton").IsEnabled&&!Named<Button>(manager.Window,"RefreshButton").IsEnabled,"Duplicate login was queried or its pending retry capability was lost");
                    Assert((await File.ReadAllBytesAsync(fixture.Native.PathFor("claude"))).SequenceEqual(defaultLogin)&&(await File.ReadAllBytesAsync(Path.Combine(pending.ClaudeConfigDirectory!,".credentials.json"))).SequenceEqual(Credential("shared-identity-fixture")),"Duplicate rejection rewrote the default login or deleted the selected profile credentials");
                    foreach(var language in new[]{"zh-CN","en-US"}){Localizer.SetLanguage(language);await Idle();Assert(Named<TextBlock>(manager.Window,"Feedback").Text==Localizer.T(DuplicateMessage),"Duplicate evidence feedback is missing or untranslated");HorizontalFit(manager.Window);Render(manager.Window,Path.Combine(root,$"claude-account-duplicate-{source}-{language}-150.png"));}
                    var read=await fixture.Host.CompleteClaudeAccountLoginAsync(pending.SlotId);Assert(read.Status==NativeAccountStatus.Invalid&&read.MessageKey==DuplicateMessage&&fixture.Host.Accounts.SequenceEqual(accounts),"Explicit local read bypassed duplicate identity rejection");
                    pending=pending with {Label="Edited second label fixture"};await fixture.Host.SaveAsync(pending);fixture.Host.ClaudeLoginFixture=async(config,_)=>{calls++;await WriteProfile(config,"corrected-identity-fixture");return new("completed_unverified",true);};
                    var retry=await fixture.Host.LoginClaudeAccountAsync(pending.SlotId);var ready=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId);
                    Assert(retry.Status==NativeAccountStatus.Captured&&ready.Label==pending.Label&&ready.Enabled&&ready.NativeIdentity!=first.NativeIdentity&&fixture.Claude.Calls.GetValueOrDefault(pending.SlotId)==1,"Corrected identity retry failed or changed the custom label");
                    var reauth=await fixture.Host.LoginClaudeAccountAsync(ready.SlotId);Assert(reauth.Status==NativeAccountStatus.Captured&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==ready.SlotId).NativeIdentity==ready.NativeIdentity&&fixture.Claude.Calls.GetValueOrDefault(ready.SlotId)==2,"Same-slot reauthentication was incorrectly rejected as a duplicate");
                    Assert(fixture.Host.Queries.Events.Count==0,"Duplicate identity fixtures made a real provider request");
                }
            });
            await Check("Approved Claude binding rejects a changed credential and a duplicate introduced while waiting for the saving lock",async()=>{
                await using var fixture=await Fixture.Create(root,"binding-review-race");var first=await fixture.Host.AddClaudeAccountAsync("Owner fixture");await WriteProfile(first,"owner-before-race");Assert((await fixture.Host.CompleteClaudeAccountLoginAsync(first.SlotId)).Succeeded,"Initial owner binding failed");first=fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==first.SlotId);
                var pending=await fixture.Host.AddClaudeAccountAsync("Waiting fixture");await WriteProfile(pending,"reviewed-candidate");var reviewed=fixture.Native.ForAccount(pending).Read("claude").Identity;var proposal=pending with {Enabled=true,NativeIdentity=reviewed,CredentialSource="local-oauth",CredentialRevision=Guid.NewGuid()};var settings=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));
                await WriteProfile(pending,"changed-after-review");await RejectIdentityChange(()=>fixture.Host.SaveAsync(proposal,expectedCurrent:pending,approveClaudeLogin:true));
                Assert(fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId)==pending&&(await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(settings),"Changed credential was substituted for the reviewed identity");
                await WriteProfile(pending,"reviewed-candidate");var saving=(SemaphoreSlim)(typeof(ApplicationHost).GetField("saving",BindingFlags.Instance|BindingFlags.NonPublic)??throw new InvalidOperationException("Saving fixture gate was not found")).GetValue(fixture.Host)!;await saving.WaitAsync();Task waiting;byte[] committed;
                try
                {
                    waiting=fixture.Host.SaveAsync(proposal,expectedCurrent:pending,approveClaudeLogin:true);Assert(!waiting.IsCompleted,"Binding fixture did not wait for the saving lock");
                    // Emulate an already-authorized competing slot commit while it
                    // owns the real gate, using only this synthetic host and files.
                    await WriteProfile(first,"reviewed-candidate");var owner=first with {NativeIdentity=reviewed,CredentialRevision=Guid.NewGuid()};fixture.Host.Accounts[fixture.Host.Accounts.FindIndex(a=>a.SlotId==owner.SlotId)]=owner;await fixture.Host.Settings.SaveAsync(fixture.Host.Accounts);await fixture.Host.Coordinator.ConfigureAccountAsync(fixture.Claude,owner);committed=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));
                }
                finally{saving.Release();}
                await RejectIdentityChange(()=>waiting);Assert(fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId)==pending&&(await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(committed)&&fixture.Claude.Calls.GetValueOrDefault(pending.SlotId)==0,"Duplicate identity passed the guarded commit or queried the rejected slot");
                Assert(fixture.Host.Queries.Events.Count==0,"Binding review fixtures made a real provider request");
            });
            await Check("Duplicate identity rejection preserves an UNKNOWN receipt and its profile credentials",async()=>{
                await using var fixture=await Fixture.Create(root,"duplicate-unknown");var first=await fixture.Host.AddClaudeAccountAsync("Known owner fixture");await WriteProfile(first,"shared-unknown-fixture");Assert((await fixture.Host.CompleteClaudeAccountLoginAsync(first.SlotId)).Succeeded,"Initial known owner binding failed");var pending=await fixture.Host.AddClaudeAccountAsync("Uncertain second fixture");await WriteProfile(pending,"shared-unknown-fixture");
                var receipt=JsonSerializer.SerializeToUtf8Bytes(new{schema_version=1,slot_id=pending.SlotId.ToString("D"),config_dir_sha256=ProtectedClaudeLoginLauncher.DirectoryHash(pending.ClaudeConfigDirectory!),status="unknown",process_closed=true,cli_started=true,credential_changed=true,credential_change_verified=true});var receiptPath=Path.Combine(pending.ClaudeConfigDirectory!,".bridge-auth-login-state.json");await File.WriteAllBytesAsync(receiptPath,receipt);var settings=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));
                var read=await fixture.Host.CompleteClaudeAccountLoginAsync(pending.SlotId);Assert(read.Status==NativeAccountStatus.Invalid&&read.MessageKey==DuplicateMessage&&fixture.Host.IsClaudeLoginUnknown(pending.SlotId)&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending.SlotId)==pending,"Duplicate read enabled the uncertain profile or cleared UNKNOWN");
                Assert((await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receipt)&&(await File.ReadAllBytesAsync(Path.Combine(pending.ClaudeConfigDirectory!,".credentials.json"))).SequenceEqual(Credential("shared-unknown-fixture"))&&(await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(settings)&&fixture.Claude.Calls.GetValueOrDefault(pending.SlotId)==0,"Duplicate rejection deleted credentials, rewrote UNKNOWN/settings or queried the uncertain slot");
            });
            await Check("Closing the Claude manager during login safely retains pending state and late feedback does not reopen it",async()=>{
                await using var fixture=await Fixture.Create(root,"closed");var gate=new TaskCompletionSource<ClaudeLoginOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);AccountConfig? pending=null;var calls=0;
                fixture.Host.ClaudeLoginFixture=(config,_)=>{pending=config;calls++;return gate.Task;};
                using var manager=new Manager(fixture,"claude");var window=manager.Window;Named<TextBox>(window,"NewLabel").Text="Closed window fixture";Click(Named<Button>(window,"AddButton"));await Until(()=>pending is not null);
                var feedback=Named<TextBlock>(window,"Feedback").Text;window.Close();gate.SetResult(new("failed",true));await Task.Delay(100);await Idle();
                Assert(!window.IsVisible&&Named<TextBlock>(window,"Feedback").Text==feedback&&!Application.Current.Windows.OfType<AccountsWindow>().Any(w=>ReferenceEquals(w,window)),"Late completion wrote/reopened a closed manager");
                Assert(calls==1&&fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==pending!.SlotId).Enabled==false&&fixture.Claude.Calls.Count==0,"Window close retried, dropped or enabled the pending record");
            });
            await Check("Other providers and legacy Claude captures retain their account actions",async()=>{
                await using var fixture=await Fixture.Create(root,"legacy");using(var manager=new Manager(fixture,"claude"))
                {
                    Assert(!Named<Button>(manager.Window,"LoginButton").IsVisible&&Named<Button>(manager.Window,"CaptureButton").Content as string==Localizer.T("Save current CLI login"),"Legacy Claude slot was made into an isolated login");
                    Click(Named<Button>(manager.Window,"CaptureButton"));await Until(()=>Named<Button>(manager.Window,"AddButton").IsEnabled);
                    Assert(fixture.Host.GetActiveAccount("claude").NativeAuthRef is not null&&fixture.Host.GetActiveAccount("claude").ClaudeConfigDirectory is null,"Legacy capture no longer saves its default fixture login");
                }
                foreach(var provider in new[]{"codex","tripo"})
                {
                    using var manager=new Manager(fixture,provider);Click(Named<Button>(manager.Window,"AddButton"));await Until(()=>fixture.Host.GetAccounts(provider).Count==2&&Named<Button>(manager.Window,"AddButton").IsEnabled);
                    Assert(fixture.Host.GetAccounts(provider).Single(a=>!a.IsActive).ClaudeConfigDirectory is null&&!Named<Button>(manager.Window,"LoginButton").IsVisible,"Ordinary Add unexpectedly requested a Claude profile");
                }
                Assert(fixture.Host.Queries.Events.Count==0,"Legacy/provider fixtures sent a query");
            });
            await Check("Read-only settings disable Claude Add and native profile actions",async()=>{
                await using var fixture=await Fixture.Create(root,"readonly");var profile=await fixture.Host.AddClaudeAccountAsync("Readonly fixture");await fixture.Host.SelectAccountAsync("claude",profile.SlotId);
                await File.WriteAllTextAsync(Path.Combine(fixture.Host.Root,"settings.json"),"{ invalid fixture");await fixture.Host.Settings.LoadAsync();Assert(fixture.Host.Settings.ReadOnly,"Read-only fixture was not established");
                var calls=0;fixture.Host.ClaudeLoginFixture=(_,_)=>{calls++;return Task.FromResult(new ClaudeLoginOutcome("failed",true));};using var manager=new Manager(fixture,"claude");
                foreach(var name in new[]{"AddButton","NewLabel","SelectButton","EditButton","DeleteButton","LoginButton","CaptureButton","SwitchButton"})Assert(!Named<UIElement>(manager.Window,name).IsEnabled,"Read-only settings left an editable/native action enabled: "+name);
                Click(Named<Button>(manager.Window,"AddButton"));Click(Named<Button>(manager.Window,"LoginButton"));await Idle();Assert(calls==0&&fixture.Host.GetAccounts("claude").Count==2,"Read-only controls launched or added an account");
                var settings=await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"));var login=await fixture.Host.LoginClaudeAccountAsync(profile.SlotId);var read=await fixture.Host.CompleteClaudeAccountLoginAsync(profile.SlotId);
                Assert(login.Status==NativeAccountStatus.Failed&&read.Status==NativeAccountStatus.Failed&&calls==0&&fixture.Claude.Calls.Count==0,"Direct read-only host Login/Complete launched, bound or queried a profile");
                Assert((await File.ReadAllBytesAsync(Path.Combine(fixture.Host.Root,"settings.json"))).SequenceEqual(settings)&&!fixture.Host.GetAccounts("claude").Single(a=>a.SlotId==profile.SlotId).Enabled,"Read-only host entry points rewrote settings or enabled the pending profile");
            });
        }
        finally{Localizer.SetLanguage("en-US");}
        await File.WriteAllTextAsync(Path.Combine(root,"claude-account-ui-results.json"),JsonSerializer.Serialize(new{passed=results.Count-failed,failed,results,scope="Actual WPF/host fixtures only; no native CLI, browser authorization, provider request or model call"},new JsonSerializerOptions{WriteIndented=true}));
        if(failed>0)throw new InvalidOperationException($"Claude account UI checks failed: {failed} of {results.Count}");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required ApplicationHost Host;public required NativeOAuthStore Native;public required FixtureAdapter[] Adapters;public required PresentationPreferencesService Preferences;public required ProviderStateHub Hub;
        public FixtureAdapter Claude=>Adapters.Single(a=>a.Definition.Id=="claude");
        public static async Task<Fixture> Create(string root,string name,Func<bool>? nativeBusy=null)
        {
            var folder=Path.Combine(root,"claude-account-ui-fixtures",name+"-"+Guid.NewGuid().ToString("N"));var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            Directory.CreateDirectory(Path.GetDirectoryName(native.PathFor("claude"))!);await File.WriteAllBytesAsync(native.PathFor("claude"),Credential("default"));
            return await Open(folder,native,nativeBusy);
        }
        public static Task<Fixture> Reopen(string folder)=>Open(folder,new NativeOAuthStore(Path.Combine(folder,"home")),null);
        private static async Task<Fixture> Open(string folder,NativeOAuthStore native,Func<bool>? nativeBusy)
        {
            var adapters=new[]{new FixtureAdapter("claude","Claude"),new FixtureAdapter("codex","Codex"),new FixtureAdapter("tripo","Tripo")};var host=new ApplicationHost(folder,adapters,native,nativeCliBusyGuard:_=>nativeBusy?.Invoke()==true);await host.InitializeAsync();
            var preferences=new PresentationPreferencesService(new PresentationStore(folder),new());var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
            return new(){Host=host,Native=native,Adapters=adapters,Preferences=preferences,Hub=hub};
        }
        public async ValueTask DisposeAsync(){Hub.Dispose();Preferences.Dispose();await Host.DisposeAsync();}
    }
    private sealed class Manager : IDisposable
    {
        public AccountsWindow Window{get;}
        public Manager(Fixture fixture,string provider){Window=new(fixture.Host,fixture.Hub,provider){ShowActivated=false,ShowInTaskbar=false};Window.Show();Window.UpdateLayout();}
        public void Dispose(){if(Window.IsVisible)Window.Close();}
    }
    private sealed class FixtureAdapter(string id,string name) : IProviderAdapter
    {
        public ProviderDefinition Definition{get;}=new(id,name,"Fixture","Synthetic login UI fixture","#ECAA88",new("invalid.test",443,"/unused"));
        public Dictionary<Guid,int> Calls{get;}=[];
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct){Calls[config.SlotId]=Calls.GetValueOrDefault(config.SlotId)+1;return Task.FromResult(new AdapterOutcome([new("five_hour","5-hour remaining",67,"%",MetricKind.Quota,100)]));}
    }
    private static byte[] Credential(string suffix)=>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken="nonfunctional-access-"+suffix,refreshToken="nonfunctional-refresh-"+suffix,expiresAt=4070908800000L}});
    private static async Task WriteProfile(AccountConfig config,string? suffix=null){Directory.CreateDirectory(config.ClaudeConfigDirectory!);await File.WriteAllBytesAsync(Path.Combine(config.ClaudeConfigDirectory!,".credentials.json"),Credential(suffix??config.SlotId.ToString("N")));}
    private static bool CurrentClaudeBinding(ApplicationHost host,AccountConfig config)=>(bool)(typeof(ApplicationHost).GetMethod("CurrentClaudeBinding",BindingFlags.Instance|BindingFlags.NonPublic)??throw new InvalidOperationException("CurrentClaudeBinding fixture probe was not found")).Invoke(host,[config])!;
    private const string DuplicateMessage="This login uses the same saved credential identity as another Claude account. Check the intended account on the authorization page. The selected account binding was not changed.";
    private static async Task RejectIdentityChange(Func<Task> action){try{await action();}catch(QueryException error)when(error.Kind==FailureKind.IdentityChanged){return;}throw new InvalidOperationException("Unsafe approved identity binding was not rejected");}
    private static T Named<T>(AccountsWindow window,string name)where T:class=>(T)window.FindName(name);
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> predicate){var timer=Stopwatch.StartNew();while(!predicate()){if(timer.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("Claude fixture UI did not settle");await Task.Delay(20);await Idle();}await Idle();}
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject{if(root is T value)yield return value;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void HorizontalFit(FrameworkElement root){foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible&&t.ActualWidth>0)){var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));Assert(bounds.Left>=-1&&bounds.Right<=root.ActualWidth+1,"Text escaped the account layout: "+text.Text);}}
    private static void Render(FrameworkElement element,string path){element.UpdateLayout();const double scale=1.5;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}
