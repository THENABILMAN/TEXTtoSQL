# Opens the real window with a database, asks two questions, reads the answer box back.
param([string]$Shot)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class W{[DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);[DllImport("user32.dll")]public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out R r);[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,string l);[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,int m,IntPtr w,IntPtr l);public struct R{public int l,t,r,b;}}
"@
$p=Start-Process C:\TEXTtoSQL\pendrive\TextToSQL\TextToSQL.exe 'C:\Users\Nabil\Downloads\cse311_lab_06_database.sql' -PassThru
$sw=[Diagnostics.Stopwatch]::StartNew()
$A=[Windows.Automation.AutomationElement]
function Root { $p.Refresh(); $A::FromHandle($p.MainWindowHandle) }
function Has($t){ $c=New-Object Windows.Automation.PropertyCondition($A::NameProperty,$t); try{ (Root).FindFirst('Descendants',$c) }catch{} }
while(-not (Has 'Ready.') -and $sw.Elapsed.TotalSeconds -lt 120){Start-Sleep 1}
"ready after $([int]$sw.Elapsed.TotalSeconds)s"
$all=(Root).FindAll('Descendants',[Windows.Automation.Condition]::TrueCondition)
$box=$all|?{$_.Current.ClassName -match '\.EDIT\.'}|select -First 1
$btn=$all|?{$_.Current.ClassName -match 'BUTTON' -and $_.Current.Name -eq 'Ask'}|select -First 1
"box $($box.Current.ClassName) btn $($btn.Current.NativeWindowHandle)"
$txt="1. Show the First_Name and Salary_Amount of the 3 highest paid employees.`r`n2. What is the difference between WHERE and HAVING?"
[void][W]::SendMessage([IntPtr]$box.Current.NativeWindowHandle,0x0C,[IntPtr]::Zero,$txt)
[void][W]::PostMessage([IntPtr]$btn.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
Start-Sleep 2; $sw.Restart()
while(-not (Has 'Ready.') -and $sw.Elapsed.TotalSeconds -lt 120){Start-Sleep 1}
"answered after $([int]$sw.Elapsed.TotalSeconds)s"
$r=New-Object W+R; [void][W]::GetWindowRect($p.MainWindowHandle,[ref]$r)
$b=New-Object Drawing.Bitmap(($r.r-$r.l),($r.b-$r.t)); $g=[Drawing.Graphics]::FromImage($b); $dc=$g.GetHdc(); [void][W]::PrintWindow($p.MainWindowHandle,$dc,2); $g.ReleaseHdc($dc); $b.Save($Shot)
Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" | %{ "$($_.Name) $([int]($_.WorkingSetSize/1MB)) MB, peak $([int]($_.PeakWorkingSetSize/1KB)) MB" }
"app $([int]($p.WorkingSet64/1MB)) MB"
[void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000); "exited: $($p.HasExited); temp left: $(Test-Path "$env:TEMP\TextToSQL_$($p.Id)")"
