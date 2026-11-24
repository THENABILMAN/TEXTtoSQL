# Opens the real window with NO database, pastes a scenario, presses "Design from scenario", then asks a question on the result.
param([string]$Shot)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class W{[DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int n);[DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out R r);[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,string l);[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,int m,IntPtr w,IntPtr l);public struct R{public int l,t,r,b;}}
"@
$p=Start-Process C:\TEXTtoSQL\pendrive\TextToSQL\TextToSQL.exe -PassThru
$sw=[Diagnostics.Stopwatch]::StartNew(); $A=[Windows.Automation.AutomationElement]
function All { $p.Refresh(); if($p.MainWindowHandle -ne 0){ try{ $A::FromHandle($p.MainWindowHandle).FindAll('Descendants',[Windows.Automation.Condition]::TrueCondition) }catch{} } }
function WaitReady($s){ $t=[Diagnostics.Stopwatch]::StartNew(); while($t.Elapsed.TotalSeconds -lt $s){ if(All | ?{$_.Current.Name -like 'Ready.*'}){return "ready after $([int]$t.Elapsed.TotalSeconds)s"}; Start-Sleep 1 }; 'TIMEOUT' }
function Put($t){ [void][W]::SendMessage([IntPtr]$box.Current.NativeWindowHandle,0x0C,[IntPtr]::Zero,$t) }
function Press($n){ $b=All|?{$_.Current.Name -eq $n -and $_.Current.ClassName -match 'BUTTON'}|select -First 1; if(!$b){"no button $n"}else{[void][W]::PostMessage([IntPtr]$b.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)} }
function Tables { [void][W]::ShowWindow($p.MainWindowHandle,9); Start-Sleep 3; 'tables: ' + ((All|?{$_.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem'}|%{$_.Current.Name}) -join ' | ') }
WaitReady 120
$box=All|?{$_.Current.ClassName -match '\.EDIT\.'}|select -First 1
"buttons: " + ((All|?{$_.Current.ClassName -match 'BUTTON'}|%{$_.Current.Name}) -join ' | ')
Put 'Show all nurses.'; Press 'Ask'; Start-Sleep 1
"ask without db: " + ((All|?{$_.Current.Name -like '*Open a database*'}|select -First 1).Current.Name)
Put ([IO.File]::ReadAllText('C:\TEXTtoSQL\eval\design\medicare.txt')); Press 'Design from scenario'; Start-Sleep 3; "design: $(WaitReady 180)"; Tables
Put 'Show the Name of each nurse and their Dept_Name.'; Press 'Ask'; Start-Sleep 3; "question: $(WaitReady 120)"
Tables
$r=New-Object W+R; [void][W]::GetWindowRect($p.MainWindowHandle,[ref]$r)
$b=New-Object Drawing.Bitmap(($r.r-$r.l),($r.b-$r.t)); $g=[Drawing.Graphics]::FromImage($b); $dc=$g.GetHdc(); [void][W]::PrintWindow($p.MainWindowHandle,$dc,2); $g.ReleaseHdc($dc); $b.Save($Shot)
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000); "exited: $($p.HasExited)"
