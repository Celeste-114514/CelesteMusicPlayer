using System;
using CelesteMusicPlayer;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
Check(TaskbarPlayerLayout.TryPlace(0,1920,1,false,1800,0,out int x,out int w)&&x==8&&w==360,"Centered taskbar left placement");
Check(TaskbarPlayerLayout.TryPlace(0,1920,1,true,1700,0,out x,out w)&&x==1332&&x+w<1700,"Left alignment preserves notification area");
Check(TaskbarPlayerLayout.TryPlace(-1920,0,1,true,-220,-1920,out x,out w)&&x==-588,"Negative monitor origin");
Check(TaskbarPlayerLayout.TryPlace(0,3840,2,false,3500,300,out x,out w)&&x==316&&w==720,"DPI applied exactly once, Widgets reserved");
Check(!TaskbarPlayerLayout.TryPlace(0,640,1,false,600,0,out x,out w),"Hide rather than cover central apps");
Check(!TaskbarPlayerLayout.TryPlace(0,640,2,true,600,0,out x,out w),"Hide oversized player");
Check(!TaskbarPlayerLayout.TryPlace(0,1920,double.NaN,false,1800,0,out x,out w),"Reject invalid geometry");
foreach(double scale in new[]{1.0,1.25,1.5,2.0})
 foreach(bool left in new[]{false,true})
  if(TaskbarPlayerLayout.TryPlace(0,3840,scale,left,3500,0,out x,out w))
   Check(x>=0&&x+w<=3840&&(!left||x+w<3500),"Player stays within taskbar and outside tray");
Console.WriteLine("PASS: taskbar alignment, negative origins, DPI, Widgets and insufficient space");
