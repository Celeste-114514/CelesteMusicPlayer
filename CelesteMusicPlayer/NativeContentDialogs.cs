// Native confirmation/input dialogs for business paths retained from the service adapter.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;
namespace Microsoft.UI.Xaml.Controls {
 public sealed partial class ContentDialog {
  private Forms.Form? _nativeDialog;
  private Task<ContentDialogResult> ShowNativeAsync() {
   using var form=new Forms.Form { Text=Convert.ToString((object?)Title) ?? "Celeste", Width=520, Height=390, MinimizeBox=false, MaximizeBox=false,
    ShowInTaskbar=false, StartPosition=Forms.FormStartPosition.CenterParent, FormBorderStyle=Forms.FormBorderStyle.FixedDialog };
   _nativeDialog=form; Result=ContentDialogResult.None;
   var sync=new List<Action>();
   var content=new Forms.FlowLayoutPanel { Dock=Forms.DockStyle.Fill, FlowDirection=Forms.FlowDirection.TopDown, WrapContents=false, AutoScroll=true, Padding=new Forms.Padding(18) };
   void Add(object? value) {
    if(value==null)return;
    if(value is TextBox text) {
     var input=new Forms.TextBox { Text=text.Text, Width=450, Multiline=text.AcceptsReturn, ReadOnly=text.IsReadOnly };
     if(input.Multiline)input.Height=140; content.Controls.Add(input);sync.Add(()=>text.Text=input.Text);return;
    }
    if(value is CheckBox check) { var input=new Forms.CheckBox { Text=Convert.ToString((object?)check.Content) ?? check.Text, AutoSize=true, Checked=check.IsChecked==true };content.Controls.Add(input);sync.Add(()=>check.IsChecked=input.Checked);return; }
    if(value is ComboBox combo) {
     var input=new Forms.ComboBox { Width=450, DropDownStyle=Forms.ComboBoxStyle.DropDownList };
     foreach(var item in combo.Items) input.Items.Add(item is ComboBoxItem ci ? Convert.ToString((object?)ci.Content) ?? "" : Convert.ToString(item) ?? "");
     if(combo.SelectedIndex>=0&&combo.SelectedIndex<input.Items.Count)input.SelectedIndex=combo.SelectedIndex;
     content.Controls.Add(input);sync.Add(()=>combo.SelectedIndex=input.SelectedIndex);return;
    }
    if(value is Border border) { Add(border.Child);return; }
    if(value is Microsoft.UI.Xaml.UIElement element) {
     if(!string.IsNullOrEmpty(element.Text))Add(element.Text);
     else if(element.Content!=null)Add((object)element.Content);
     foreach(var child in element.Children)Add(child);return;
    }
    content.Controls.Add(new Forms.Label { Text=Convert.ToString(value) ?? "", AutoSize=true, MaximumSize=new System.Drawing.Size(450,0), Margin=new Forms.Padding(0,0,0,12) });
   }
   Add((object?)Content);
   var buttons=new Forms.FlowLayoutPanel { Dock=Forms.DockStyle.Bottom, AutoSize=true, FlowDirection=Forms.FlowDirection.RightToLeft, Padding=new Forms.Padding(12) };
   Forms.Button Button(string text,ContentDialogResult result,bool enabled) {
    var button=new Forms.Button { Text=text, AutoSize=true, Enabled=enabled, MinimumSize=new System.Drawing.Size(85,30) };
    button.Click+=(_,_)=>{foreach(var action in sync)action();Result=result;form.Close();};buttons.Controls.Add(button);return button;
   }
   var close=Button(Convert.ToString((object?)CloseButtonText) is {Length:>0} closeText ? closeText : "关闭",ContentDialogResult.None,true);form.CancelButton=close;
   if(Convert.ToString((object?)SecondaryButtonText) is {Length:>0} secondary)Button(secondary,ContentDialogResult.Secondary,IsSecondaryButtonEnabled);
   if(Convert.ToString((object?)PrimaryButtonText) is {Length:>0} primary)form.AcceptButton=Button(primary,ContentDialogResult.Primary,IsPrimaryButtonEnabled);
   form.Controls.Add(content);form.Controls.Add(buttons);
   try { nint hwnd=CelesteMusicPlayer.MainWindow.PickerHostHandle();if(hwnd!=0)form.ShowDialog(new Owner(hwnd));else form.ShowDialog(); }
   finally { _nativeDialog=null; }
   return Task.FromResult(Result);
  }
  private sealed class Owner(nint hwnd) : Forms.IWin32Window { public nint Handle => hwnd; }
 }
}
