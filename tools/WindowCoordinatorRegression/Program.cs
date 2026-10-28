using System;
using System.Collections.Generic;
using CelesteMusicPlayer;

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
var surface = new Surface();var coordinator = new MainWindowCoordinator(surface);
coordinator.Show("settings");
Check(string.Join(',',surface.Events)=="create,show,activate,hide-owner,navigate:settings","Create/display/navigation order changed");
surface.Events.Clear();surface.IsMinimized=true;coordinator.Show();
Check(string.Join(',',surface.Events)=="restore,show,activate,hide-owner","Minimized restore must precede show");
surface.Events.Clear();coordinator.Show();
Check(string.Join(',',surface.Events)=="show,activate,hide-owner","Existing window must be reused");
surface.Events.Clear();surface.OnClose=()=>coordinator.Show("settings");coordinator.Close();coordinator.Close();coordinator.Show();
Check(coordinator.IsClosing&&string.Join(',',surface.Events)=="close","Closing must prevent reactivation and recursion");
var reentrant=new Surface();var second=new MainWindowCoordinator(reentrant);
reentrant.OnCreate=()=>second.Show("settings");second.Show();
Check(string.Join(',',reentrant.Events)=="create,show,activate,hide-owner,navigate:settings","Reentrant show must not create two windows or lose navigation");
var failing=new Surface { FailCreate=true };var third=new MainWindowCoordinator(failing);
try { third.Show();throw new Exception("Expected create failure"); } catch(InvalidOperationException) { }
failing.FailCreate=false;third.Show();Check(failing.Exists&&failing.Events.FindAll(x=>x=="create").Count==2,"Failed creation must permit retry");
var unopened=new Surface();new MainWindowCoordinator(unopened).Close();Check(unopened.Events.Count==0,"Shutdown must not create a window");
Console.WriteLine("PASS: create/reuse/minimized/hidden navigation order, reentry, close suppression and creation retry");

sealed class Surface : IMainWindowSurface
{
 public bool Exists{get;private set;} public bool IsMinimized{get;set;} public bool FailCreate;
 public Action? OnCreate,OnClose; public List<string> Events{get;}=new();
 public void Create(){Events.Add("create");if(FailCreate)throw new InvalidOperationException();Exists=true;OnCreate?.Invoke();}
 public void Restore(){Events.Add("restore");IsMinimized=false;}
 public void Show()=>Events.Add("show");public void Activate()=>Events.Add("activate");public void HideLegacyOwner()=>Events.Add("hide-owner");
 public void Navigate(string page)=>Events.Add("navigate:"+page);
 public void Close(){Events.Add("close");Exists=false;OnClose?.Invoke();}
}
