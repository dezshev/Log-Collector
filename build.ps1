param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskBuild = Join-Path $taskRoot 'build'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$taskReferences = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework C# compiler not found.' }
if (-not (Test-Path -LiteralPath $taskReferences)) { throw '.NET Framework 4.8 targeting pack is required (Visual Studio Installer).' }
New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
$taskFlags = @('/nologo', '/noconfig', '/langversion:5', '/warn:4', '/warnaserror+', '/optimize+', '/nostdlib+',
    "/reference:$taskReferences\mscorlib.dll", "/reference:$taskReferences\System.dll", "/reference:$taskReferences\System.Core.dll")
function Invoke-TaskCompile {
    param([string]$Folder, [string]$Output, [string]$Kind, [string[]]$References)
    $taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot $Folder) -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { $_.FullName })
    $taskArguments = $taskFlags + @("/target:$Kind", "/out:$taskBuild\$Output")
    foreach ($taskReference in $References) { $taskArguments += "/reference:$taskBuild\$taskReference" }
    if ($Folder -eq 'src\LogCollector.AdminWpf') {
        $taskArguments += "/resource:$taskRoot\$Folder\MainView.xaml,LogCollector.AdminWpf.MainView.xaml"
    }
    & $taskCompiler @taskArguments @taskSources
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $Output" }
    if ($Kind -eq 'exe' -or $Kind -eq 'winexe') {
        Copy-Item -LiteralPath (Join-Path $taskRoot "$Folder\App.config") -Destination "$taskBuild\$Output.config"
    }
    Write-Output "Built $Output"
}
Invoke-TaskCompile 'src\LogCollector.Common' 'LogCollector.Common.dll' 'library' @()
Invoke-TaskCompile 'src\LogCollector.Client' 'LogCollector.Client.dll' 'library' @('LogCollector.Common.dll')
Invoke-TaskCompile 'src\LogCollector.Server' 'LogCollector.Server.exe' 'exe' @('LogCollector.Common.dll')
Invoke-TaskCompile 'src\LogCollector.DemoAgent' 'LogCollector.DemoAgent.exe' 'exe' @('LogCollector.Common.dll', 'LogCollector.Client.dll')
Invoke-TaskCompile 'src\LogCollector.AdminConsole' 'LogCollector.AdminConsole.exe' 'exe' @('LogCollector.Common.dll', 'LogCollector.Client.dll')
$taskFlags += @("/reference:$taskReferences\WindowsBase.dll", "/reference:$taskReferences\PresentationCore.dll",
    "/reference:$taskReferences\PresentationFramework.dll", "/reference:$taskReferences\System.Xaml.dll", "/reference:$taskReferences\System.Xml.dll")
Invoke-TaskCompile 'src\LogCollector.AdminWpf' 'LogCollector.AdminWpf.exe' 'winexe' @('LogCollector.Common.dll', 'LogCollector.Client.dll')
Invoke-TaskCompile 'tests\LogCollector.Tests' 'LogCollector.Tests.exe' 'exe' @('LogCollector.Common.dll', 'LogCollector.Client.dll', 'LogCollector.Server.exe')
Invoke-TaskCompile 'tests\LogCollector.UiTests' 'LogCollector.UiTests.exe' 'exe' @('LogCollector.Common.dll', 'LogCollector.Client.dll', 'LogCollector.Server.exe', 'LogCollector.AdminWpf.exe')
if ($Test) {
    & (Join-Path $taskBuild 'LogCollector.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & (Join-Path $taskBuild 'LogCollector.UiTests.exe') (Join-Path $taskRoot 'docs\testing\screenshots')
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
}
